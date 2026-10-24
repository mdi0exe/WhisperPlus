using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Whisper.net;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Identifies the spoken language using Whisper's own language head, via its <em>own</em> small
/// GGML model (<see cref="AppSettings.LanguageDetectorModel"/>, "tiny" by default) rather than
/// borrowing <see cref="WhisperService"/>'s: the point is to know the language *before* the real —
/// possibly multi-gigabyte — transcription model has finished loading, and to work even when the
/// audio is destined for Vosk, which cannot identify languages at all.
///
/// <para>This is the fallback backend. Whisper's language head is a side capability of a model
/// trained to transcribe, and its training set is dominated by English, so it answers "en" readily
/// and confidently — including for audio that carries no speech at all. Restricting the candidate
/// list mitigates but does not remove that bias, which is why
/// <see cref="SileroLanguageIdentifier"/> is preferred when its model is available.</para>
/// </summary>
public sealed class WhisperLanguageIdentifier : ILanguageIdentifier
{
    /// <summary>Capture rate the whole app uses; Whisper requires exactly this.</summary>
    private const int SampleRate = 16000;

    private readonly WhisperModelManager _models;
    private readonly ISettingsService _settings;
    private readonly object _gate = new();

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _loadedModelId;
    private Task<bool>? _loadingTask;
    private string? _loadingModelId;
    private bool _disposed;

    public WhisperLanguageIdentifier(WhisperModelManager models, ISettingsService settings)
    {
        _models = models;
        _settings = settings;
    }

    /// <summary>The model id currently configured as the detector.</summary>
    public string ModelId => _settings.Current.LanguageDetectorModel;

    public string Name => $"Whisper {ModelId}";

    /// <summary>
    /// With much less than a second of speech Whisper's answer is close to a coin flip. It pads to a
    /// full 30 s mel internally regardless.
    /// </summary>
    public int MinWindowMs => 1000;

    /// <summary>Past ~2 s the answer stops improving and only costs latency.</summary>
    public int MaxWindowMs => 2000;

    public bool IsInstalled => _models.IsInstalled(ModelId);

    public bool IsReady
    {
        get { lock (_gate) return _processor is not null; }
    }

    public Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        var modelId = ModelId;
        lock (_gate)
        {
            if (_loadedModelId == modelId && _processor is not null) return Task.FromResult(true);
            if (_loadingTask is not null && _loadingModelId == modelId) return _loadingTask;

            var task = Task.Run(() => Load(modelId), ct);
            _loadingModelId = modelId;
            _loadingTask = task;
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

    private bool Load(string modelId)
    {
        lock (_gate)
        {
            if (_loadedModelId == modelId && _processor is not null) return true;
            if (!_models.IsInstalled(modelId))
            {
                Log.Warning("Language-detector model {Id} is not installed.", modelId);
                return false;
            }

            ReleaseInternal();
            try
            {
                _factory = WhisperFactory.FromPath(_models.PathFor(modelId), new WhisperFactoryOptions
                {
                    UseGpu = true,            // falls back to CPU via the runtime package
                    UseFlashAttention = false,
                });
                // "auto" is required: a processor pinned to one language has nothing to detect.
                _processor = _factory.CreateBuilder().WithLanguage("auto").Build();
                _loadedModelId = modelId;
                Log.Information("Whisper language identifier loaded: {Id}", modelId);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load language-detector model {Id}.", modelId);
                ReleaseInternal();
                return false;
            }
        }
    }

    public LanguageObservation? Observe(
        float[] samples,
        IReadOnlyList<string> candidates,
        ObservationContext context)
    {
        if (samples.Length == 0) return null;

        // Normalize and de-duplicate the candidate codes; Whisper ignores ones it doesn't know,
        // but an all-unknown list would silently widen detection to every language it supports.
        var codes = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var code = LanguageCatalog.Normalize(candidate);
            if (code.Length == 2 && !codes.Contains(code)) codes.Add(code);
        }
        if (codes.Count == 0) return null;

        // A single candidate has only one possible answer — skip the encoder pass entirely.
        if (codes.Count == 1)
            return new LanguageObservation(
                codes, new[] { 1.0 }, 1.0,
                context.StartMs, SamplesToMs(samples.Length), context.VoicedRatio);

        WhisperProcessor processor;
        lock (_gate)
        {
            if (_processor is null) return null;
            processor = _processor;
        }

        var window = LanguageIdWindow.Build(samples, SampleRate, MinWindowMs, MaxWindowMs);
        var speechMs = SamplesToMs(Math.Min(samples.Length, MsToSamples(MaxWindowMs)));
        try
        {
            var (language, probability) = processor.DetectLanguageWithProbability(window, codes.ToArray());
            var code = LanguageCatalog.Normalize(language);
            if (code.Length != 2) return null;

            Log.Debug("Whisper LID: {Code} ({Probability:P0}) from {Ms} ms of audio at +{Offset} ms.",
                code, probability, speechMs, context.StartMs);

            // Whisper.net reports only the winner and its probability, so the rest of the distribution
            // is filled in at maximum entropy — see LanguageObservation.FromWinner. The in-domain mass
            // is reported as 1 because the language head, restricted to a candidate set, has no way to
            // say "none of these": it renormalizes over whatever it was given and answers anyway. That
            // missing signal is the single biggest reason Silero is the preferred backend, and it is
            // why the accumulator applies a standing bias correction to this backend's English answers
            // (see LanguageEvidenceAccumulator's prior) rather than trusting them at face value.
            return LanguageObservation.FromWinner(
                code, probability, codes, inDomainMass: 1.0,
                context.StartMs, speechMs, context.VoicedRatio);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Whisper language detection failed.");
            return null;
        }
    }

    private static int MsToSamples(int ms) => SampleRate * ms / 1000;

    private static int SamplesToMs(int samples) => samples <= 0 ? 0 : samples * 1000 / SampleRate;

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
