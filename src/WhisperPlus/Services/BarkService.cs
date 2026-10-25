using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SherpaOnnx;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Runs the Bark engine: NVIDIA's Parakeet-TDT transducer (the architecture Bark 3.0 Pro is built
/// on) through sherpa-onnx's <see cref="OfflineRecognizer"/>. The recognizer is built lazily for
/// the active model folder and cached, because it owns the ONNX sessions and is the expensive
/// part; <see cref="Unload"/> releases the ~650 MB it holds.
///
/// <para>Unlike <see cref="NemotronService"/> this is an <em>offline</em> model: there is no
/// streaming decoder and no endpointing, so a window of audio goes in and text comes out. The
/// dictation session therefore drives it the way it drives Whisper — re-decoding a rolling window
/// while the user talks — rather than feeding it once.</para>
/// </summary>
public sealed class BarkService : IDisposable
{
    /// <summary>Every Parakeet export is 16 kHz — the same rate the capture uses.</summary>
    private const int SampleRate = 16000;

    /// <summary>
    /// Nominal feature dimension. sherpa-onnx overrides this from the encoder's own metadata for
    /// NeMo exports, so it only has to be a sane starting value.
    /// </summary>
    private const int FeatureDim = 80;

    private readonly BarkModelManager _models;
    private readonly object _gate = new();

    private OfflineRecognizer? _recognizer;
    private string? _loadedModelId;
    private bool _disposed;

    /// <summary>
    /// The load currently running for <see cref="_loadingModelId"/>, so several callers asking for
    /// the same model share one load instead of building two recognizers.
    /// </summary>
    private Task<bool>? _loadingTask;
    private string? _loadingModelId;

    public BarkService(BarkModelManager models)
    {
        _models = models;
    }

    /// <summary>True if a Bark recognizer is currently loaded in memory.</summary>
    public bool IsLoaded => _recognizer is not null;

    /// <summary>The model id currently loaded, or null if nothing loaded.</summary>
    public string? LoadedModelId => _loadedModelId;

    /// <summary>Whether the model folder is on disk.</summary>
    public bool IsModelAvailable(string modelId) => _models.IsInstalled(modelId);

    /// <summary>
    /// Ensures the recognizer for <paramref name="modelId"/> is built, swapping out a different
    /// model if one is loaded. Returns false if the folder is incomplete or sherpa-onnx refuses it.
    /// </summary>
    public bool EnsureLoaded(string modelId)
    {
        lock (_gate)
        {
            if (_loadedModelId == modelId && _recognizer is not null) return true;
            if (!_models.IsInstalled(modelId))
            {
                Log.Warning("Bark model {Id} is not installed.", modelId);
                return false;
            }

            // Swap: release the previous recognizer first to avoid holding two in RAM.
            ReleaseInternal();

            try
            {
                _recognizer = new OfflineRecognizer(BuildConfig(modelId));
                _loadedModelId = modelId;
                Log.Information("Bark model loaded: {Id}", modelId);
                return true;
            }
            catch (Exception ex)
            {
                // A missing native sherpa-onnx DLL surfaces here as a DllNotFoundException, which
                // must not take the dictation session down with it.
                Log.Error(ex, "Failed to load Bark model {Id}.", modelId);
                ReleaseInternal();
                return false;
            }
        }
    }

    /// <summary>
    /// Builds the sherpa-onnx configuration for a model folder. <c>ModelType</c> is left empty on
    /// purpose: sherpa-onnx identifies the NeMo transducer implementation from the decoder's
    /// outputs and metadata, so naming a type here could only ever contradict the export.
    /// </summary>
    private OfflineRecognizerConfig BuildConfig(string modelId)
    {
        var config = new OfflineRecognizerConfig();

        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = FeatureDim;

        config.ModelConfig.Transducer.Encoder = _models.FilePathFor(modelId, BarkModelCatalog.EncoderFile);
        config.ModelConfig.Transducer.Decoder = _models.FilePathFor(modelId, BarkModelCatalog.DecoderFile);
        config.ModelConfig.Transducer.Joiner = _models.FilePathFor(modelId, BarkModelCatalog.JoinerFile);
        config.ModelConfig.Tokens = _models.FilePathFor(modelId, BarkModelCatalog.TokensFile);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;

        // Named rather than auto-detected: the Bark conversion is a NeMo transducer with the same
        // graph layout as NVIDIA's Parakeet exports, and saying so removes any chance of the
        // detector guessing a different family from the decoder's outputs.
        config.ModelConfig.ModelType = "nemo_transducer";

        // Half the cores, capped: dictation shares the machine with whatever the user is typing
        // into, and the encoder is the heavy part.
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

        // The TDT exports only implement greedy search.
        config.DecodingMethod = "greedy_search";

        return config;
    }

    /// <summary>
    /// Loads the model for <paramref name="modelId"/> on a background thread, so a caller on the
    /// UI thread never blocks on reading 650 MB of ONNX. Concurrent calls for the same model share
    /// one load.
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
    /// Decodes one window of 16 kHz mono audio and returns its text as timed segments, so the
    /// caller can apply the same rolling-commit rule it uses for Whisper.
    /// </summary>
    public Task<IReadOnlyList<TranscribedSegment>> TranscribeSegmentsAsync(
        float[] samples,
        CancellationToken ct = default)
    {
        if (samples is null || samples.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<TranscribedSegment>>(Array.Empty<TranscribedSegment>());
        }

        // The native decode blocks and takes no token, so it runs off the caller's thread; the
        // token is honoured before the work starts and again before the result is handed back.
        return Task.Run<IReadOnlyList<TranscribedSegment>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            OfflineRecognizer recognizer;
            lock (_gate)
            {
                recognizer = _recognizer
                    ?? throw new InvalidOperationException("No Bark model loaded. Call EnsureLoaded first.");
            }

            using var stream = recognizer.CreateStream();

            // Bark was evaluated — and its WER numbers measured — with a quarter second of silence
            // in front of each utterance, and the conversion's own usage notes ask for it: without
            // the pad the first word is sometimes clipped, because the encoder's receptive field
            // starts mid-phoneme. The pad shifts every timestamp by the same amount, so the
            // segment split subtracts it again before the caller sees the times.
            var padded = new float[samples.Length + LeadSilenceSamples];
            samples.CopyTo(padded, LeadSilenceSamples);
            stream.AcceptWaveform(SampleRate, padded);

            lock (_gate)
            {
                // Decode holds the native session; holding the gate keeps a concurrent Unload from
                // freeing the recognizer underneath it.
                recognizer.Decode(stream);
            }

            ct.ThrowIfCancellationRequested();

            var result = stream.Result;
            return SplitIntoSegments(result, LeadSilenceSeconds);
        }, ct);
    }

    /// <summary>Convenience wrapper for callers that only want the text.</summary>
    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        var segments = await TranscribeSegmentsAsync(samples, ct).ConfigureAwait(false);
        var text = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment.Text.Length == 0) continue;
            if (text.Length > 0) text.Append(' ');
            text.Append(segment.Text);
        }
        return text.ToString();
    }

    /// <summary>Gap that ends a segment: a pause long enough that the model has clearly moved on.</summary>
    private const float SegmentGapSeconds = 0.6f;

    /// <summary>
    /// Silence prepended to every decode, matching how Bark was evaluated. See the call site.
    /// </summary>
    private const float LeadSilenceSeconds = 0.25f;

    private const int LeadSilenceSamples = (int)(SampleRate * LeadSilenceSeconds);

    /// <summary>
    /// Turns the token stream into segments by cutting on pauses, and rebuilds each segment's text
    /// from its own tokens. Token timestamps are what make the rolling commit possible at all —
    /// the recognizer returns one flat string otherwise, with no way to tell which part of the
    /// window has settled.
    ///
    /// <para>Falls back to a single segment spanning the whole result when the tokens carry no
    /// usable text, so a text that decodes fine but tokenizes oddly still reaches the user.</para>
    /// </summary>
    /// <param name="leadSilenceSeconds">
    /// How much silence was prepended to the audio before decoding. Timestamps are measured from
    /// the start of the padded audio, so this is subtracted back out — otherwise every committed
    /// window would claim a quarter second more audio than it consumed and the rolling window
    /// would skip ahead, silently dropping speech.
    /// </param>
    private static IReadOnlyList<TranscribedSegment> SplitIntoSegments(
        OfflineRecognizerResult result, float leadSilenceSeconds)
    {
        var segments = new List<TranscribedSegment>();

        var tokens = result.Tokens;
        var times = result.Timestamps;

        if (tokens is { Length: > 0 } && times is { Length: > 0 })
        {
            var text = new StringBuilder();
            var start = times[0] - leadSilenceSeconds;
            var end = start;

            for (var i = 0; i < tokens.Length && i < times.Length; i++)
            {
                var t = times[i];
                var token = tokens[i] ?? "";

                // A pause since the previous token closes the current segment.
                if (text.Length > 0 && t - end > SegmentGapSeconds)
                {
                    AddSegment(segments, text, start, end);
                    text.Clear();
                    start = t - leadSilenceSeconds;
                }

                text.Append(token);
                end = t - leadSilenceSeconds;
            }

            AddSegment(segments, text, start, end);
        }

        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(result.Text))
        {
            var seconds = times is { Length: > 0 } ? times[^1] - leadSilenceSeconds : 0f;
            segments.Add(new TranscribedSegment(
                Normalize(result.Text),
                TimeSpan.Zero,
                TimeSpan.FromSeconds(Math.Max(0f, seconds))));
        }

        return segments;
    }

    private static void AddSegment(List<TranscribedSegment> segments, StringBuilder text, float startSeconds, float endSeconds)
    {
        var normalized = Normalize(text.ToString());
        if (normalized.Length == 0) return;

        // A token's timestamp is its start; give the segment a small nominal length so a
        // zero-width span cannot look like it ended before it began.
        var end = Math.Max(endSeconds, startSeconds);
        segments.Add(new TranscribedSegment(
            normalized,
            TimeSpan.FromSeconds(Math.Max(0, startSeconds)),
            TimeSpan.FromSeconds(Math.Max(0, end))));
    }

    /// <summary>
    /// Collapses the sentencepiece word marker and repeated spaces. NeMo exports mark a word start
    /// with U+2581; joins that already carry a plain space are left alone.
    ///
    /// <para>Also drops the literal <c>&lt;unk&gt;</c> token. sherpa-onnx emits it for words the
    /// vocabulary lacks, where the reference PyTorch decoder simply leaves the token out — so it
    /// would otherwise appear in the transcript as visible noise.</para>
    /// </summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var source = text.Replace("<unk>", "", StringComparison.Ordinal);

        var sb = new StringBuilder(source.Length);
        var lastWasSpace = true;

        foreach (var ch in source)
        {
            var c = ch == '\u2581' ? ' ' : ch;
            if (c == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
                sb.Append(c);
            }
            else
            {
                lastWasSpace = false;
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
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
