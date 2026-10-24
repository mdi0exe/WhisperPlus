using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperPlus.Services;

/// <summary>One language-identification result: the winning code and how confident the model was.</summary>
public readonly record struct LanguageGuess(string Code, float Probability);

/// <summary>
/// A model that can name the language being spoken in a short window of audio.
///
/// <para>Two implementations exist for a reason. Whisper's language head
/// (<see cref="WhisperLanguageIdentifier"/>) is free if the user already has a small GGML model,
/// but it is a side capability of an ASR model and carries a punishing English prior. Silero's
/// classifier (<see cref="SileroLanguageIdentifier"/>) is trained for this one job. Both are
/// exposed through this interface so the choice is a setting rather than a rewrite, and so the
/// session's detection logic is written once.</para>
///
/// <para><see cref="Observe"/> is blocking and CPU-bound; call it from a background thread.</para>
/// </summary>
public interface ILanguageIdentifier : IDisposable
{
    /// <summary>Short human-readable name for logs and the widget's status line.</summary>
    string Name { get; }

    /// <summary>
    /// Whether the backing model is on disk — a cheap check a caller can make on the UI thread
    /// before starting a session, so a missing model degrades to "no routing" instead of stalling
    /// dictation.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>True when the model is loaded and ready to answer.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Shortest window worth asking about. A shorter one is zero-padded up to it, since every
    /// backend needs some minimum to build features from.
    /// </summary>
    int MinWindowMs { get; }

    /// <summary>
    /// Longest window used. Past this the answer stops improving and only costs latency, so
    /// <see cref="Detect"/> examines no more than this much of the audio it is handed.
    /// </summary>
    int MaxWindowMs { get; }

    /// <summary>
    /// Loads the model on a background thread; concurrent calls share one load. Returns false when
    /// the model is missing or unloadable, in which case the caller should carry on without routing.
    /// </summary>
    Task<bool> EnsureReadyAsync(CancellationToken ct = default);

    /// <summary>
    /// Scores which of <paramref name="candidates"/> is being spoken in <paramref name="samples"/>
    /// (16 kHz mono float32). Restricting the answer to the user's actual languages is both faster
    /// and far more accurate than choosing from every language the model knows. Returns null when
    /// the model isn't loaded, no candidate is representable, or the audio is not plausibly any of
    /// them.
    ///
    /// <para>Returns the whole distribution rather than just the winner, because the decision is made
    /// by <see cref="LanguageEvidenceAccumulator"/> across several windows and it needs the margin
    /// over the runner-up and the quality signals — not an argmax that has already thrown them
    /// away.</para>
    /// </summary>
    LanguageObservation? Observe(
        float[] samples,
        IReadOnlyList<string> candidates,
        ObservationContext context);

    /// <summary>
    /// The single most likely language in <paramref name="samples"/>, for a caller that wants one
    /// answer from one window and has no utterance to accumulate evidence over.
    /// </summary>
    LanguageGuess? Detect(float[] samples, IReadOnlyList<string> candidates) =>
        Observe(samples, candidates, ObservationContext.Unknown)?.Best;

    /// <summary>Releases the model, freeing its memory. Safe to call repeatedly.</summary>
    void Unload();
}
