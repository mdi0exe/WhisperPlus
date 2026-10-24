using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SherpaOnnx;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Wraps NVIDIA's Nemotron 3.5 streaming ASR (a cache-aware FastConformer-RNNT) via sherpa-onnx:
/// lazily builds the recognizer for the active model folder on first use and hands out
/// <see cref="NemotronStream"/>s for live dictation. The recognizer is cached — it owns the ONNX
/// sessions and is the expensive part — while a fresh stream is created per utterance; call
/// <see cref="Unload"/> (or change the active model) to release the ~650 MB it holds.
///
/// <para>This is the accurate streaming option: unlike Vosk it is a single multilingual model
/// covering ~35 languages with punctuation and capitalization, and unlike Whisper it decodes as
/// the audio arrives instead of re-running on the whole buffer. The trade-offs are the download
/// size and that latency is fixed at export time by the model's chunk size.</para>
/// </summary>
public sealed class NemotronService : IDisposable
{
    /// <summary>These exports are all 16 kHz — the same rate the capture uses.</summary>
    private const int SampleRate = 16000;

    /// <summary>
    /// Nominal feature dimension. The NeMo transducer path in sherpa-onnx overrides this from the
    /// encoder's own metadata, so it only has to be a sane starting value rather than a number we
    /// have to keep in sync with the export.
    /// </summary>
    private const int FeatureDim = 80;

    private readonly NemotronModelManager _models;
    private readonly object _gate = new();

    private OnlineRecognizer? _recognizer;
    private string? _loadedModelId;
    private bool _disposed;

    /// <summary>
    /// The load currently running for <see cref="_loadingModelId"/>, so several callers asking
    /// for the same model share one load instead of building two recognizers.
    /// </summary>
    private Task<bool>? _loadingTask;
    private string? _loadingModelId;

    public NemotronService(NemotronModelManager models)
    {
        _models = models;
    }

    /// <summary>True if a Nemotron recognizer is currently loaded in memory.</summary>
    public bool IsLoaded => _recognizer is not null;

    /// <summary>The model id currently loaded, or null if nothing loaded.</summary>
    public string? LoadedModelId => _loadedModelId;

    /// <summary>
    /// Whether the model folder is on disk — a cheap check callers can make on the UI thread
    /// before starting a session, so the expensive <see cref="EnsureLoaded"/> can be deferred
    /// to a background thread.
    /// </summary>
    public bool IsModelAvailable(string modelId) => _models.IsInstalled(modelId);

    /// <summary>
    /// Ensures the recognizer for <paramref name="modelId"/> is built. If a different model is
    /// loaded, it is swapped out. Returns false if the model folder is incomplete or sherpa-onnx
    /// refuses to load it.
    /// </summary>
    public bool EnsureLoaded(string modelId)
    {
        lock (_gate)
        {
            if (_loadedModelId == modelId && _recognizer is not null) return true;
            if (!_models.IsInstalled(modelId))
            {
                Log.Warning("Nemotron model {Id} is not installed.", modelId);
                return false;
            }

            // Swap: release the previous recognizer first to avoid holding two in RAM.
            ReleaseInternal();

            try
            {
                _recognizer = new OnlineRecognizer(BuildConfig(modelId));
                _loadedModelId = modelId;
                Log.Information("Nemotron model loaded: {Id}", modelId);
                return true;
            }
            catch (Exception ex)
            {
                // A missing native sherpa-onnx DLL surfaces here as a DllNotFoundException, which
                // must not take the dictation session down with it — the caller falls back to
                // reporting that the model could not be loaded.
                Log.Error(ex, "Failed to load Nemotron model {Id}.", modelId);
                ReleaseInternal();
                return false;
            }
        }
    }

    /// <summary>
    /// Builds the sherpa-onnx configuration for a model folder. <c>ModelType</c> is deliberately
    /// left empty: sherpa-onnx picks the NeMo transducer implementation by inspecting the
    /// decoder's outputs and metadata, so naming a type here could only ever contradict the
    /// export.
    /// </summary>
    private OnlineRecognizerConfig BuildConfig(string modelId)
    {
        var config = new OnlineRecognizerConfig();

        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = FeatureDim;

        config.ModelConfig.Transducer.Encoder = _models.FilePathFor(modelId, NemotronModelCatalog.EncoderFile);
        config.ModelConfig.Transducer.Decoder = _models.FilePathFor(modelId, NemotronModelCatalog.DecoderFile);
        config.ModelConfig.Transducer.Joiner = _models.FilePathFor(modelId, NemotronModelCatalog.JoinerFile);
        config.ModelConfig.Tokens = _models.FilePathFor(modelId, NemotronModelCatalog.TokensFile);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;

        // Half the cores, capped: the encoder is the heavy part and dictation shares the machine
        // with whatever the user is actually typing into.
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

        // The Nemotron streaming transducer only implements greedy search — modified_beam_search
        // (and therefore hotwords) is not available for this architecture.
        config.DecodingMethod = "greedy_search";

        // Endpointing is what makes the live preview settle: without it the whole dictation stays
        // one growing hypothesis, and nothing is ever committed until the user stops talking.
        config.EnableEndpoint = 1;

        return config;
    }

    /// <summary>
    /// Loads the model for <paramref name="modelId"/> on a background thread, so a caller on the
    /// UI thread never blocks on reading 650 MB of ONNX. Concurrent calls for the same model
    /// share one load. Returns false if the model folder is missing or fails to load.
    /// </summary>
    public Task<bool> EnsureLoadedAsync(string modelId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_loadedModelId == modelId && _recognizer is not null) return Task.FromResult(true);
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
    /// Opens a live recognizer stream over the loaded model. <paramref name="languageCode"/> is
    /// the 2-letter code routing has settled on, or null to let the model identify the language
    /// itself. The caller owns the returned stream and must dispose it.
    /// </summary>
    public NemotronStream CreateStream(string? languageCode = null)
    {
        lock (_gate)
        {
            var recognizer = _recognizer
                ?? throw new InvalidOperationException("No Nemotron model loaded. Call EnsureLoaded first.");
            return new NemotronStream(recognizer, languageCode);
        }
    }

    /// <summary>Releases the recognizer, freeing RAM. Safe to call repeatedly.</summary>
    public void Unload()
    {
        lock (_gate) ReleaseInternal();
    }

    private void ReleaseInternal()
    {
        try { _recognizer?.Dispose(); } catch { /* ignore */ }
        _recognizer = null;
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
/// A live Nemotron recognizer stream for one dictation session: audio is fed in as it is captured,
/// and finalized utterances come back from <see cref="Feed"/> as soon as sherpa-onnx detects an
/// endpoint. <see cref="Partial"/> exposes the in-progress hypothesis for the widget's live
/// preview, and <see cref="Finish"/> flushes whatever is left when the user stops talking.
///
/// <para>Not thread-safe: the decoder carries the encoder's cache state and expects its audio in
/// order, so all calls must come from one thread (the session's pump task).</para>
/// </summary>
public sealed class NemotronStream : IDisposable
{
    /// <summary>
    /// Silence appended before the final decode. Cache-aware streaming only emits a chunk once
    /// enough right-context has arrived, so without a tail of padding the last word or two of a
    /// dictation never leaves the encoder.
    /// </summary>
    private const float TailPaddingSeconds = 0.5f;

    /// <summary>The per-stream option sherpa-onnx reads to pick the model's language prompt.</summary>
    private const string LanguageOption = "language";

    /// <summary>Value that asks the model to identify the language itself.</summary>
    private const string AutoLanguage = "auto";

    private const int SampleRate = 16000;

    private readonly OnlineRecognizer _recognizer;
    private readonly OnlineStream _stream;
    private bool _disposed;

    internal NemotronStream(OnlineRecognizer recognizer, string? languageCode)
    {
        _recognizer = recognizer;
        _stream = recognizer.CreateStream();

        // Only the multilingual exports expose the prompt input, so the option is probed rather
        // than assumed — an English-only export simply has no "language" to set.
        try
        {
            if (_stream.HasOption(LanguageOption))
            {
                var value = string.IsNullOrWhiteSpace(languageCode) ? AutoLanguage : languageCode.Trim();
                _stream.SetOption(LanguageOption, value);
                Log.Debug("Nemotron stream language prompt set to {Language}.", value);
            }
            else if (!string.IsNullOrWhiteSpace(languageCode))
            {
                Log.Debug("Nemotron model does not expose a language prompt; ignoring {Language}.", languageCode);
            }
        }
        catch (Exception ex)
        {
            // A stream that cannot take the hint still transcribes — it just auto-detects.
            Log.Warning(ex, "Could not set the Nemotron language prompt; continuing on auto.");
        }
    }

    /// <summary>
    /// Feeds newly captured samples to the recognizer and returns any utterances it finalized
    /// while consuming them — usually none, occasionally one, so the list stays tiny.
    /// </summary>
    public List<string> Feed(ReadOnlySpan<float> samples)
    {
        var finalized = new List<string>();
        if (_disposed || samples.IsEmpty) return finalized;

        // The native API takes an array rather than a span, so the copy is unavoidable.
        _stream.AcceptWaveform(SampleRate, samples.ToArray());
        DrainDecoder();

        // An endpoint means a sentence ended: take its text and rewind the decoder for the next
        // one, exactly as Vosk hands back a finalized utterance.
        if (_recognizer.IsEndpoint(_stream))
        {
            var text = _recognizer.GetResult(_stream).Text?.Trim();
            if (!string.IsNullOrEmpty(text)) finalized.Add(text);
            _recognizer.Reset(_stream);
        }

        return finalized;
    }

    /// <summary>The current in-progress hypothesis, or an empty string when there is none.</summary>
    public string Partial()
    {
        if (_disposed) return "";
        return _recognizer.GetResult(_stream).Text?.Trim() ?? "";
    }

    /// <summary>Flushes the recognizer and returns the last utterance's text (possibly empty).</summary>
    public string Finish()
    {
        if (_disposed) return "";

        _stream.AcceptWaveform(SampleRate, new float[(int)(SampleRate * TailPaddingSeconds)]);
        _stream.InputFinished();
        DrainDecoder();

        return _recognizer.GetResult(_stream).Text?.Trim() ?? "";
    }

    /// <summary>Decodes every chunk the encoder has enough audio for.</summary>
    private void DrainDecoder()
    {
        while (_recognizer.IsReady(_stream)) _recognizer.Decode(_stream);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); } catch { /* ignore */ }
    }
}
