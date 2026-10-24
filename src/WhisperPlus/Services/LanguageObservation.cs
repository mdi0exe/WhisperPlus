using System;
using System.Collections.Generic;

namespace WhisperPlus.Services;

/// <summary>
/// What the caller knows about a window that the identifier cannot work out for itself: where the
/// audio sits in the utterance, and how much of it was speech.
///
/// <para>Both belong to whoever sliced the buffer — the identifier is handed a bare float array with
/// no idea where it came from — and both matter to how the answer is weighed, so they travel with
/// the request instead of being guessed at afterwards.</para>
/// </summary>
/// <param name="StartMs">Offset of the window from the speech onset, in milliseconds.</param>
/// <param name="VoicedRatio">Fraction of the window's frames that carried speech (1 if unmeasured).</param>
public readonly record struct ObservationContext(int StartMs, double VoicedRatio)
{
    /// <summary>For a one-off call with no utterance to place the window in.</summary>
    public static ObservationContext Unknown => new(0, 1.0);
}

/// <summary>
/// One look at one stretch of audio by one language identifier: the full distribution over the
/// candidate languages, plus everything needed to judge how much that distribution is worth.
///
/// <para>This replaces the older "winning code + probability" pair as what a backend reports.
/// Collapsing a distribution to its argmax throws away the two things the decision actually needs:
/// the <em>margin</em> over the runner-up (which is what separates "confident" from "the other
/// options were merely worse"), and the evidence that the audio was worth classifying at all.
/// Silero, for instance, computes exactly how much of its 95-way softmax landed on the user's
/// languages and then discarded that number after using it as a yes/no gate — an observation holding
/// 11% of the mass and one holding 99% were treated as equally trustworthy.</para>
///
/// <para>Immutable and free of audio, so <see cref="LanguageEvidenceAccumulator"/> can be tested
/// against hand-written observations without a model or a wave file.</para>
/// </summary>
public sealed class LanguageObservation
{
    private readonly string[] _codes;
    private readonly double[] _probabilities;

    /// <param name="codes">Candidate codes this observation scored, in any order.</param>
    /// <param name="probabilities">
    /// Probability per code, aligned with <paramref name="codes"/>. Normalized on construction, so a
    /// caller may pass unnormalized masses.
    /// </param>
    /// <param name="inDomainMass">
    /// Share of the model's <em>full</em> output distribution that the candidates hold — how much the
    /// audio looks like one of the user's languages at all. 1 for a backend that cannot report it.
    /// </param>
    /// <param name="startMs">Where the window began, in milliseconds from the speech onset.</param>
    /// <param name="lengthMs">Milliseconds of real audio examined, excluding any zero padding.</param>
    /// <param name="voicedRatio">Fraction of the window's frames that carried speech rather than pause.</param>
    public LanguageObservation(
        IReadOnlyList<string> codes,
        IReadOnlyList<double> probabilities,
        double inDomainMass,
        int startMs,
        int lengthMs,
        double voicedRatio)
    {
        if (codes.Count == 0) throw new ArgumentException("An observation needs at least one code.", nameof(codes));
        if (codes.Count != probabilities.Count)
            throw new ArgumentException("Codes and probabilities must be the same length.", nameof(probabilities));

        _codes = new string[codes.Count];
        _probabilities = new double[codes.Count];

        var total = 0.0;
        for (var i = 0; i < codes.Count; i++)
        {
            _codes[i] = codes[i];
            // A negative "probability" is a caller bug, but clamping is cheaper than a crash on the
            // dictation hot path.
            var value = Math.Max(0.0, probabilities[i]);
            _probabilities[i] = value;
            total += value;
        }

        if (total > 0)
        {
            for (var i = 0; i < _probabilities.Length; i++) _probabilities[i] /= total;
        }
        else
        {
            // No mass anywhere: the only honest reading is "no preference".
            for (var i = 0; i < _probabilities.Length; i++) _probabilities[i] = 1.0 / _probabilities.Length;
        }

        InDomainMass = Math.Clamp(inDomainMass, 0.0, 1.0);
        StartMs = Math.Max(0, startMs);
        LengthMs = Math.Max(0, lengthMs);
        VoicedRatio = Math.Clamp(voicedRatio, 0.0, 1.0);
    }

    /// <summary>The candidate codes, in the order they were scored.</summary>
    public IReadOnlyList<string> Codes => _codes;

    /// <summary>Normalized probability per candidate, aligned with <see cref="Codes"/>.</summary>
    public IReadOnlyList<double> Probabilities => _probabilities;

    /// <summary>How much of the model's whole output the candidates held; see the constructor.</summary>
    public double InDomainMass { get; }

    /// <summary>Start of the examined window, in milliseconds from the speech onset.</summary>
    public int StartMs { get; }

    /// <summary>Length of real (unpadded) audio examined, in milliseconds.</summary>
    public int LengthMs { get; }

    /// <summary>End of the examined window, in milliseconds from the speech onset.</summary>
    public int EndMs => StartMs + LengthMs;

    /// <summary>Fraction of the window that carried speech rather than pause.</summary>
    public double VoicedRatio { get; }

    /// <summary>The most likely candidate, and its probability — for logging and for the reduced
    /// <see cref="LanguageGuess"/> the older callers still expect.</summary>
    public LanguageGuess Best
    {
        get
        {
            var bestIndex = 0;
            for (var i = 1; i < _probabilities.Length; i++)
                if (_probabilities[i] > _probabilities[bestIndex]) bestIndex = i;
            return new LanguageGuess(_codes[bestIndex], (float)_probabilities[bestIndex]);
        }
    }

    /// <summary>Probability assigned to <paramref name="code"/>, or 0 if it was not scored.</summary>
    public double ProbabilityOf(string code)
    {
        for (var i = 0; i < _codes.Length; i++)
            if (string.Equals(_codes[i], code, StringComparison.Ordinal)) return _probabilities[i];
        return 0.0;
    }

    /// <summary>
    /// Builds an observation from a single winner and its probability, spreading the leftover mass
    /// evenly over the rest.
    ///
    /// <para>For a backend whose API only reports the top answer — Whisper's language head does —
    /// the uniform spread is the maximum-entropy completion: it is the least committal distribution
    /// consistent with what was actually reported, so it neither invents a runner-up ordering the
    /// model never gave nor pretends the alternatives were ruled out.</para>
    /// </summary>
    public static LanguageObservation FromWinner(
        string code,
        double probability,
        IReadOnlyList<string> candidates,
        double inDomainMass,
        int startMs,
        int lengthMs,
        double voicedRatio)
    {
        var probabilities = new double[candidates.Count];
        var winner = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!string.Equals(candidates[i], code, StringComparison.Ordinal)) continue;
            winner = i;
            break;
        }

        // The winner is not in the candidate list: nothing can be said about the candidates, so say
        // nothing rather than smuggling in a language the caller did not ask about.
        if (winner < 0)
        {
            for (var i = 0; i < probabilities.Length; i++) probabilities[i] = 1.0;
            return new LanguageObservation(candidates, probabilities, inDomainMass, startMs, lengthMs, voicedRatio);
        }

        var top = Math.Clamp(probability, 0.0, 1.0);
        var rest = candidates.Count > 1 ? (1.0 - top) / (candidates.Count - 1) : 0.0;
        for (var i = 0; i < probabilities.Length; i++) probabilities[i] = i == winner ? top : rest;

        return new LanguageObservation(candidates, probabilities, inDomainMass, startMs, lengthMs, voicedRatio);
    }
}
