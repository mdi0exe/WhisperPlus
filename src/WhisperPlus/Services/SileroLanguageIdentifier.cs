using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;
using WhisperPlus.Models;
// This namespace already owns a SessionOptions (the dictation session's), which would otherwise
// shadow the ONNX Runtime type of the same name.
using OnnxSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace WhisperPlus.Services;

/// <summary>
/// Identifies the spoken language with Silero's 95-language classifier (ONNX, ~17 MB, MIT).
///
/// <para>This is the preferred backend. Unlike Whisper's language head it was trained for exactly
/// this task, so it has no reason to prefer English, and at 17 MB it loads fast enough to be ready
/// before the user finishes their first word. The graph takes raw 16 kHz mono float32 samples
/// shaped <c>[batch, samples]</c> — no mel spectrogram, no normalization, no feature extraction of
/// any kind — and returns one logit per language.</para>
///
/// <para>Silero's own accuracy is modest when asked to pick freely among all 95 languages,
/// especially on short utterances. This implementation does not ask it to: it takes the full
/// softmax, sums the mass belonging to each language the user actually dictates in, and renormalizes
/// over just those. Choosing between two well-separated languages is a far easier question than
/// choosing among 95, and the restriction is applied to probabilities rather than by editing the
/// label map, so the model's own calibration is preserved.</para>
/// </summary>
public sealed class SileroLanguageIdentifier : ILanguageIdentifier
{
    /// <summary>Capture rate the whole app uses; the model expects exactly this.</summary>
    private const int SampleRate = 16000;

    /// <summary>
    /// Minimum share of the full 95-way softmax that the user's candidate languages must hold
    /// before the restricted answer is trusted.
    ///
    /// <para>Renormalizing over two candidates always produces a winner, even when the audio is
    /// neither of them — so without this check silence yields a confident-looking verdict. Measured
    /// against real recordings, the separation is stark: Persian speech puts 97.7% of the mass on
    /// {fa, en} and English 99.9%, while German speech leaves 0.2% and room tone 0.16% (its mass
    /// lands on unrelated languages entirely — Norwegian Nynorsk, in one sample). 10% sits roughly
    /// two orders of magnitude clear of both sides, so it rejects "not one of your languages" and
    /// "not speech at all" without ever second-guessing a real utterance.</para>
    /// </summary>
    private const double MinCandidateMass = 0.10;

    private readonly LanguageIdModelManager _models;
    private readonly object _gate = new();

    private InferenceSession? _session;
    private Task<bool>? _loadingTask;
    private bool _disposed;

    public SileroLanguageIdentifier(LanguageIdModelManager models)
    {
        _models = models;
    }

    public string Name => "Silero lang95";

    /// <summary>
    /// The model handles arbitrary lengths, but a very short clip gives its pooling layers little to
    /// average over, so a floor of one second is enforced by padding.
    /// </summary>
    public int MinWindowMs => 1000;

    /// <summary>
    /// Silero was trained on clips under 20 s and keeps improving well past Whisper's useful 2 s, so
    /// it is given a wider window — the extra audio is nearly free at this model size, and short
    /// utterances are exactly where it needs the help.
    /// </summary>
    public int MaxWindowMs => 4000;

    public bool IsInstalled => _models.IsInstalled;

    public bool IsReady
    {
        get { lock (_gate) return _session is not null; }
    }

    public Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_session is not null) return Task.FromResult(true);
            if (_loadingTask is not null) return _loadingTask;

            var task = Task.Run(Load, ct);
            _loadingTask = task;
            _ = task.ContinueWith(finished =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_loadingTask, finished)) _loadingTask = null;
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return task;
        }
    }

    private bool Load()
    {
        lock (_gate)
        {
            if (_session is not null) return true;
            if (!_models.IsInstalled)
            {
                Log.Warning("Silero language-ID model is not installed at {Path}.", _models.ModelPath);
                return false;
            }

            ReleaseInternal();
            try
            {
                // Deliberately single-threaded: the model is small enough that extra threads buy
                // nothing, and the cores are wanted by the transcription model decoding in parallel.
                var options = new OnnxSessionOptions
                {
                    IntraOpNumThreads = 1,
                    InterOpNumThreads = 1,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,

                    // The model was exported with a batch size of 8 baked into the static shape of
                    // its language-*group* output, so running a batch of 1 makes the runtime warn
                    // about a shape mismatch on every single inference. That output is not read
                    // here and the language output is unaffected, so the warning is pure noise —
                    // and at one per dictation it would bury everything else in the log.
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                };
                _session = new InferenceSession(_models.ModelPath, options);
                Log.Information("Silero language identifier loaded from {Path}.", _models.ModelPath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load the Silero language-ID model.");
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

        // Map each candidate to the output classes representing it. A language the model has no
        // class for cannot be chosen, so it is dropped rather than silently scored as zero.
        var targets = new List<(string Code, int[] Indices)>(candidates.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var code = LanguageCatalog.Normalize(candidate);
            if (code.Length != 2 || !seen.Add(code)) continue;

            var indices = LanguageIdModelCatalog.IndicesFor(code);
            if (indices is null)
            {
                Log.Debug("Silero has no class for {Code}; excluding it from detection.", code);
                continue;
            }
            targets.Add((code, indices));
        }
        if (targets.Count == 0) return null;

        // One candidate has only one possible answer — skip inference entirely.
        if (targets.Count == 1)
            return new LanguageObservation(
                new[] { targets[0].Code }, new[] { 1.0 }, 1.0,
                context.StartMs, SamplesToMs(samples.Length), context.VoicedRatio);

        InferenceSession session;
        lock (_gate)
        {
            if (_session is null) return null;
            session = _session;
        }

        var window = LanguageIdWindow.Build(samples, SampleRate, MinWindowMs, MaxWindowMs);

        // What the window is worth is set by the real audio in it, not by the zero padding a short
        // clip was brought up to the minimum with.
        var speechMs = SamplesToMs(Math.Min(samples.Length, MsToSamples(MaxWindowMs)));
        try
        {
            var logits = Run(session, window);
            if (logits is null) return null;

            var probabilities = Softmax(logits);

            // Sum each language's classes (Chinese owns four, Swedish and Punjabi two). The
            // per-candidate masses are kept rather than reduced to a winner: the accumulator pools
            // whole distributions, and the margin between the top two is what its decision turns on.
            var codes = new string[targets.Count];
            var masses = new double[targets.Count];
            var totalMass = 0.0;
            for (var i = 0; i < targets.Count; i++)
            {
                var (code, indices) = targets[i];
                var mass = 0.0;
                foreach (var index in indices) mass += probabilities[index];
                codes[i] = code;
                masses[i] = mass;
                totalMass += mass;
            }

            if (totalMass <= 0) return null;

            // Nothing the user speaks is really in this audio: say so instead of picking a winner
            // from noise. The caller moves on to a different part of the utterance.
            if (totalMass < MinCandidateMass)
            {
                Log.Debug("Silero LID declined: candidates hold only {Share:P2} of the mass over {Ms} ms.",
                    totalMass, speechMs);
                return null;
            }

            // The mass travels with the observation instead of being consumed by the gate above.
            // Between the 10% floor and a clean reading there is a wide band where the answer is worth
            // *something* but not full trust, and only the accumulator can weigh that against the
            // other windows it has seen.
            var observation = new LanguageObservation(
                codes, masses, totalMass,
                context.StartMs, speechMs, context.VoicedRatio);

            var best = observation.Best;
            Log.Debug("Silero LID: {Code} ({Probability:P0}, candidates held {Share:P1} of the full softmax) " +
                      "from {Ms} ms of audio at +{Offset} ms.",
                best.Code, best.Probability, totalMass, speechMs, context.StartMs);
            return observation;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Silero language detection failed.");
            return null;
        }
    }

    /// <summary>
    /// Runs one forward pass and returns the per-language logits, or null if the graph produced
    /// nothing recognizable. The output is located by name first and by width second, so a
    /// re-exported model with renamed tensors still works as long as it keeps 95 classes.
    /// </summary>
    private static float[]? Run(InferenceSession session, float[] window)
    {
        var tensor = new DenseTensor<float>(window, new[] { 1, window.Length });
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor(LanguageIdModelCatalog.InputName, tensor),
        };

        using var results = session.Run(inputs);

        float[]? byWidth = null;
        foreach (var result in results)
        {
            float[] values;
            try { values = result.AsTensor<float>().ToArray(); }
            catch { continue; }   // the language-group output, or anything non-float

            if (result.Name == LanguageIdModelCatalog.OutputName) return values;
            if (values.Length == LanguageIdModelCatalog.ClassCount) byWidth ??= values;
        }

        if (byWidth is null)
            Log.Warning("Silero model produced no {Count}-class output.", LanguageIdModelCatalog.ClassCount);
        return byWidth;
    }

    private static int MsToSamples(int ms) => SampleRate * ms / 1000;

    private static int SamplesToMs(int samples) => samples <= 0 ? 0 : samples * 1000 / SampleRate;

    /// <summary>
    /// Numerically stable softmax. The graph emits raw logits, so the conversion happens here rather
    /// than in the model.
    /// </summary>
    private static double[] Softmax(float[] logits)
    {
        var max = float.NegativeInfinity;
        foreach (var logit in logits) if (logit > max) max = logit;

        var result = new double[logits.Length];
        var sum = 0.0;
        for (var i = 0; i < logits.Length; i++)
        {
            var value = Math.Exp(logits[i] - max);
            result[i] = value;
            sum += value;
        }

        if (sum <= 0) return result;
        for (var i = 0; i < result.Length; i++) result[i] /= sum;
        return result;
    }

    public void Unload()
    {
        lock (_gate) ReleaseInternal();
    }

    private void ReleaseInternal()
    {
        try { _session?.Dispose(); } catch { /* ignore */ }
        _session = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseInternal();
    }
}
