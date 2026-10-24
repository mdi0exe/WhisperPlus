using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace WhisperPlus.Services;

/// <summary>
/// Wraps the Vosk (Kaldi) engine: lazily loads the active model folder on first use and
/// exposes <see cref="TranscribeAsync"/> for raw PCM float samples (16 kHz mono). The loaded
/// <c>Model</c> is cached — it is the expensive part — while a fresh recognizer is created
/// per utterance; call <see cref="Unload"/> (or change the active model) to release RAM.
///
/// <para>Vosk is the lightweight alternative to Whisper here: ~40–60 MB models, low RAM, and
/// it recognizes as the audio streams in rather than processing the whole buffer at the end.
/// The trade-off is accuracy, and that one model = one language (no auto-detection).</para>
/// </summary>
public sealed class VoskService : IDisposable
{
    /// <summary>Vosk models in this catalog are all 16 kHz — the same rate the capture uses.</summary>
    private const float SampleRate = 16000f;

    /// <summary>Bytes of 16-bit PCM fed to the recognizer at a time (~0.25 s at 16 kHz).</summary>
    private const int ChunkBytes = 8000;

    private readonly VoskModelManager _models;
    private readonly object _gate = new();

    private global::Vosk.Model? _model;
    private string? _loadedModelId;
    private bool _disposed;

    /// <summary>
    /// The load currently running for <see cref="_loadingModelId"/>, so several callers asking
    /// for the same model share one load instead of unpacking it into RAM twice.
    /// </summary>
    private Task<bool>? _loadingTask;
    private string? _loadingModelId;

    /// <summary>Raised with each recognized utterance as it is finalized, for live display.</summary>
    public event Action<string>? SegmentReceived;

    static VoskService()
    {
        // Vosk logs Kaldi's chatter to stdout/stderr by default; -1 silences it.
        try { global::Vosk.Vosk.SetLogLevel(-1); }
        catch (Exception ex) { Log.Warning(ex, "Could not set Vosk log level."); }
    }

    public VoskService(VoskModelManager models)
    {
        _models = models;
    }

    /// <summary>True if a Vosk model is currently loaded in memory.</summary>
    public bool IsLoaded => _model is not null;

    /// <summary>The model id currently loaded, or null if nothing loaded.</summary>
    public string? LoadedModelId => _loadedModelId;

    /// <summary>
    /// Whether the model folder is on disk — a cheap check callers can make on the UI thread
    /// before starting a session, so the expensive <see cref="EnsureLoaded"/> can be deferred
    /// to a background thread.
    /// </summary>
    public bool IsModelAvailable(string modelId) => _models.IsInstalled(modelId);

    /// <summary>
    /// Ensures the model for <paramref name="modelId"/> is loaded. If a different model is
    /// loaded, it is swapped out. Returns false if the model folder is missing or unreadable.
    /// </summary>
    public bool EnsureLoaded(string modelId)
    {
        lock (_gate)
        {
            if (_loadedModelId == modelId && _model is not null) return true;
            if (!_models.IsInstalled(modelId))
            {
                Log.Warning("Vosk model {Id} is not installed.", modelId);
                return false;
            }

            // Swap: release the previous model first to avoid holding two in RAM.
            ReleaseInternal();

            try
            {
                _model = new global::Vosk.Model(_models.PathFor(modelId));
                _loadedModelId = modelId;
                Log.Information("Vosk model loaded: {Id}", modelId);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load Vosk model {Id}.", modelId);
                ReleaseInternal();
                return false;
            }
        }
    }

    /// <summary>
    /// Loads the model for <paramref name="modelId"/> on a background thread, so a caller on the
    /// UI thread never blocks on reading the model folder. Concurrent calls for the same model
    /// share one load. Returns false if the model folder is missing or fails to load.
    /// </summary>
    public Task<bool> EnsureLoadedAsync(string modelId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_loadedModelId == modelId && _model is not null) return Task.FromResult(true);
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
    /// Opens a live recognizer over the loaded model, for transcribing while the user is still
    /// speaking. This is Vosk's natural mode: audio is fed in as it is captured and utterances
    /// come back finalized one at a time, with a partial hypothesis available in between.
    /// The caller owns the returned stream and must dispose it.
    /// </summary>
    public VoskStream CreateStream()
    {
        lock (_gate)
        {
            var model = _model
                ?? throw new InvalidOperationException("No Vosk model loaded. Call EnsureLoaded first.");
            return new VoskStream(model, SampleRate);
        }
    }

    /// <summary>
    /// Transcribes 16 kHz mono float32 samples into a single concatenated string. Raises
    /// <see cref="SegmentReceived"/> per recognized utterance. Runs off the UI thread.
    /// </summary>
    public Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        global::Vosk.Model model;
        lock (_gate)
        {
            model = _model ?? throw new InvalidOperationException("No Vosk model loaded. Call EnsureLoaded first.");
        }

        return Task.Run(() =>
        {
            var pcm = ToPcm16(samples);
            var parts = new List<string>();

            global::Vosk.VoskRecognizer? recognizer = null;
            try
            {
                recognizer = new global::Vosk.VoskRecognizer(model, SampleRate);
                recognizer.SetWords(false);

                var chunk = new byte[ChunkBytes];
                for (var offset = 0; offset < pcm.Length; offset += ChunkBytes)
                {
                    ct.ThrowIfCancellationRequested();

                    var length = Math.Min(ChunkBytes, pcm.Length - offset);
                    Buffer.BlockCopy(pcm, offset, chunk, 0, length);

                    // True means Vosk decided an utterance ended, so a final result is ready.
                    if (recognizer.AcceptWaveform(chunk, length))
                    {
                        Collect(recognizer.Result(), parts);
                    }
                }

                Collect(recognizer.FinalResult(), parts);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error(ex, "Vosk transcription failed.");
                throw;
            }
            finally
            {
                try { recognizer?.Dispose(); } catch { /* ignore */ }
            }

            return string.Join(" ", parts);

            void Collect(string json, List<string> into)
            {
                var text = ReadText(json);
                if (string.IsNullOrEmpty(text)) return;
                into.Add(text);
                SegmentReceived?.Invoke(text);
            }
        }, ct);
    }

    /// <summary>
    /// Converts float samples to the 16-bit little-endian PCM buffer Vosk expects. The float
    /// overload of <c>AcceptWaveform</c> wants Kaldi's ±32768 scale rather than the ±1.0 the
    /// capture produces, so going through int16 keeps the levels — and therefore the
    /// recognition accuracy — correct.
    /// </summary>
    private static byte[] ToPcm16(float[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        WritePcm16(samples, pcm);
        return pcm;
    }

    /// <summary>
    /// Writes <paramref name="samples"/> into <paramref name="destination"/> as 16-bit
    /// little-endian PCM and returns the number of bytes written. Lets the streaming path
    /// reuse one scratch buffer for the whole session instead of allocating per audio chunk.
    /// </summary>
    internal static int WritePcm16(ReadOnlySpan<float> samples, byte[] destination)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var scaled = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            var value = (short)scaled;
            destination[i * 2] = (byte)(value & 0xFF);
            destination[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return samples.Length * 2;
    }

    /// <summary>Pulls the <c>text</c> field out of a Vosk result JSON document.</summary>
    private static string? ReadText(string? json) => ReadField(json, "text");

    /// <summary>
    /// Pulls one string field out of a Vosk result JSON document. Finalized results carry
    /// <c>text</c>; the in-progress hypothesis from <c>PartialResult</c> carries <c>partial</c>.
    /// </summary>
    internal static string? ReadField(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(field, out var value))
            {
                return value.GetString()?.Trim();
            }
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Unexpected Vosk result payload: {Json}", json);
        }
        return null;
    }

    /// <summary>Bytes of 16-bit PCM handed to the streaming recognizer at a time.</summary>
    internal const int StreamChunkBytes = ChunkBytes;

    /// <summary>Releases the model, freeing RAM. Safe to call repeatedly.</summary>
    public void Unload()
    {
        lock (_gate) ReleaseInternal();
    }

    private void ReleaseInternal()
    {
        try { _model?.Dispose(); } catch { /* ignore */ }
        _model = null;
        _loadedModelId = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseInternal();
    }
}

/// <summary>
/// A live Vosk recognizer for one dictation session: audio is fed in as it is captured, and
/// finalized utterances come back from <see cref="Feed"/> as soon as Vosk decides an utterance
/// ended. <see cref="Partial"/> exposes the in-progress hypothesis for the widget's live preview,
/// and <see cref="Finish"/> flushes whatever is left when the user stops talking.
///
/// <para>Not thread-safe: a Kaldi recognizer is stateful and expects its audio in order, so all
/// calls must come from one thread (the session's pump task).</para>
/// </summary>
public sealed class VoskStream : IDisposable
{
    private readonly global::Vosk.VoskRecognizer _recognizer;
    private readonly byte[] _scratch = new byte[VoskService.StreamChunkBytes];
    private bool _disposed;

    internal VoskStream(global::Vosk.Model model, float sampleRate)
    {
        _recognizer = new global::Vosk.VoskRecognizer(model, sampleRate);
        _recognizer.SetWords(false);
    }

    /// <summary>
    /// Feeds newly captured samples to the recognizer and returns any utterances it finalized
    /// while consuming them — usually none, occasionally one, so the list stays tiny.
    /// </summary>
    public List<string> Feed(ReadOnlySpan<float> samples)
    {
        var finalized = new List<string>();
        if (_disposed || samples.IsEmpty) return finalized;

        const int samplesPerChunk = VoskService.StreamChunkBytes / 2;
        for (var offset = 0; offset < samples.Length; offset += samplesPerChunk)
        {
            var end = Math.Min(offset + samplesPerChunk, samples.Length);
            var written = VoskService.WritePcm16(samples[offset..end], _scratch);

            // True means Vosk decided an utterance ended, so a final result is ready.
            if (_recognizer.AcceptWaveform(_scratch, written))
            {
                var text = VoskService.ReadField(_recognizer.Result(), "text");
                if (!string.IsNullOrEmpty(text)) finalized.Add(text);
            }
        }
        return finalized;
    }

    /// <summary>The current in-progress hypothesis, or an empty string when there is none.</summary>
    public string Partial()
    {
        if (_disposed) return "";
        return VoskService.ReadField(_recognizer.PartialResult(), "partial") ?? "";
    }

    /// <summary>Flushes the recognizer and returns the last utterance's text (possibly empty).</summary>
    public string Finish()
    {
        if (_disposed) return "";
        return VoskService.ReadField(_recognizer.FinalResult(), "text") ?? "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _recognizer.Dispose(); } catch { /* ignore */ }
    }
}
