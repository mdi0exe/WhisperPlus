using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Whisper.net;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// One recognized Whisper segment with the span of audio it covers, measured from the start of
/// the buffer that produced it. The timestamps are what a live session needs to decide which
/// text has settled and can be committed, and which tail is still worth re-decoding.
/// </summary>
public readonly record struct TranscribedSegment(string Text, TimeSpan Start, TimeSpan End);

/// <summary>
/// Wraps the Whisper.net engine: lazily loads the active GGML model on first use and
/// exposes <see cref="TranscribeAsync"/> for raw PCM float samples (16 kHz mono). The
/// factory/processor are created once per model and cached; call <see cref="Unload"/>
/// (or change the active model) to release RAM when the widget is hidden.
///
/// <para>Whisper requires 16 kHz mono float32 input. Callers are responsible for
/// resampling; this service accepts already-correct samples.</para>
/// </summary>
public sealed class WhisperService : IDisposable
{
    private readonly WhisperModelManager _models;
    private readonly object _gate = new();

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _loadedModelId;
    private bool _disposed;

    /// <summary>
    /// The load currently running for <see cref="_loadingModelId"/>, so several callers asking
    /// for the same model share one load instead of reading a multi-gigabyte file twice.
    /// </summary>
    private Task<bool>? _loadingTask;
    private string? _loadingModelId;

    /// <summary>Raised with partial transcription text as segments arrive.</summary>
    public event Action<string>? SegmentReceived;

    public WhisperService(WhisperModelManager models)
    {
        _models = models;
    }

    /// <summary>True if a Whisper factory is currently loaded in memory.</summary>
    public bool IsLoaded => _factory is not null;

    /// <summary>The model id currently loaded, or null if nothing loaded.</summary>
    public string? LoadedModelId => _loadedModelId;

    /// <summary>
    /// Whether the model file is on disk — a cheap check callers can make on the UI thread
    /// before starting a session, so the expensive <see cref="EnsureLoaded"/> can be deferred
    /// to a background thread.
    /// </summary>
    public bool IsModelAvailable(string modelId) => _models.IsInstalled(modelId);

    /// <summary>
    /// Ensures the model for <paramref name="modelId"/> is loaded. If a different model
    /// is loaded, it is swapped out. Returns false if the model file is missing.
    /// </summary>
    public bool EnsureLoaded(string modelId)
    {
        lock (_gate)
        {
            if (_loadedModelId == modelId && _factory is not null) return true;
            if (!_models.IsInstalled(modelId))
            {
                Log.Warning("Whisper model {Id} is not installed.", modelId);
                return false;
            }

            // Swap: release the previous model first to avoid double-loading large files.
            ReleaseInternal();
            var path = _models.PathFor(modelId);

            try
            {
                var options = new WhisperFactoryOptions
                {
                    UseGpu = true,           // auto-falls-back to CPU via the runtime package
                    UseFlashAttention = false,
                };
                _factory = WhisperFactory.FromPath(path, options);
                // The *.en models contain English only and cannot perform language
                // detection. Asking one to use "auto" throws "Failed to auto-detect
                // language" as soon as dictation stops. Multilingual models retain
                // automatic detection; English-only models are explicitly pinned.
                var language = modelId.EndsWith(".en", StringComparison.OrdinalIgnoreCase)
                    ? "en"
                    : "auto";
                _processor = _factory.CreateBuilder()
                    .WithLanguage(language)
                    .Build();
                _loadedModelId = modelId;
                Log.Information("Whisper model loaded: {Id}", modelId);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load Whisper model {Id}.", modelId);
                ReleaseInternal();
                return false;
            }
        }
    }

    /// <summary>
    /// Loads the model for <paramref name="modelId"/> on a background thread, so a caller on the
    /// UI thread never blocks on reading a multi-gigabyte file. Concurrent calls for the same
    /// model share one load. Returns false if the model file is missing or fails to load.
    /// </summary>
    public Task<bool> EnsureLoadedAsync(string modelId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_loadedModelId == modelId && _factory is not null) return Task.FromResult(true);
            if (_loadingTask is not null && _loadingModelId == modelId) return _loadingTask;

            var task = Task.Run(() => EnsureLoaded(modelId), ct);
            _loadingModelId = modelId;
            _loadingTask = task;

            // Clear the shared handle once it completes, so a later call for the same model that
            // failed (or was unloaded since) actually retries instead of replaying the old result.
            _ = task.ContinueWith(finished =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_loadingTask, finished))
                    {
                        _loadingTask = null;
                        _loadingModelId = null;
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return task;
        }
    }

    /// <summary>
    /// Pins the loaded processor to a specific language instead of re-detecting it on every
    /// call — worthwhile once language routing has already identified the language, since
    /// detection costs an extra encoder pass and can disagree with the routed model.
    /// Pass null or an unknown code to go back to automatic detection.
    /// </summary>
    /// <remarks>
    /// Mutates the processor's parameters, so it must not run while a transcription is in
    /// flight. Callers serialize it against <see cref="TranscribeSegmentsAsync"/>.
    /// </remarks>
    public void PinLanguage(string? languageCode)
    {
        lock (_gate)
        {
            if (_processor is null || _loadedModelId is null) return;

            // The *.en models only know English; the builder already pinned them to "en" and
            // asking them for anything else fails at the next call.
            if (_loadedModelId.EndsWith(".en", StringComparison.OrdinalIgnoreCase)) return;

            var code = LanguageCatalog.Normalize(languageCode);
            var language = code.Length == 2 ? code : "auto";
            try
            {
                _processor.ChangeLanguage(language);
                Log.Debug("Whisper language pinned to {Language}.", language);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not pin Whisper language to {Language}.", language);
            }
        }
    }

    /// <summary>
    /// Transcribes 16 kHz mono float32 samples into a single concatenated string.
    /// Raises <see cref="SegmentReceived"/> per recognized segment for live display.
    /// </summary>
    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        var segments = await TranscribeSegmentsAsync(samples, ct).ConfigureAwait(false);

        var parts = new List<string>(segments.Count);
        foreach (var segment in segments) parts.Add(segment.Text);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Transcribes 16 kHz mono float32 samples and returns each recognized segment with its
    /// timestamps, which is what lets a live session commit the settled part of an utterance
    /// and re-decode only the tail. Raises <see cref="SegmentReceived"/> as segments arrive.
    /// </summary>
    public async Task<IReadOnlyList<TranscribedSegment>> TranscribeSegmentsAsync(
        float[] samples, CancellationToken ct = default)
    {
        WhisperProcessor processor;
        lock (_gate)
        {
            processor = _processor
                ?? throw new InvalidOperationException("No Whisper model loaded. Call EnsureLoaded first.");
        }

        var segments = new List<TranscribedSegment>();
        try
        {
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                // Whisper.net reuses the native buffer behind SegmentData.Text after the
                // iteration moves on, so the string has to be copied out here — Trim() does that.
                var text = segment.Text?.Trim();
                if (string.IsNullOrEmpty(text)) continue;

                segments.Add(new TranscribedSegment(text, segment.Start, segment.End));
                SegmentReceived?.Invoke(text);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(ex, "Whisper transcription failed.");
            throw;
        }

        return segments;
    }

    /// <summary>Releases the model and processor, freeing RAM. Safe to call repeatedly.</summary>
    public void Unload()
    {
        lock (_gate) ReleaseInternal();
    }

    private void ReleaseInternal()
    {
        try { _processor?.Dispose(); } catch { /* ignore */ }
        try { _factory?.Dispose(); } catch { /* ignore */ }
        _processor = null;
        _factory = null;
        _loadedModelId = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseInternal();
    }
}
