using System;
using System.Collections.Generic;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// What is believed about the language before any audio is heard, expressed as a log-space nudge per
/// candidate.
///
/// <para>Two things go in. First, a <em>backend bias correction</em>: Whisper's language head is a
/// side capability of a model whose training set is dominated by English, and it answers "en" readily
/// — including for audio carrying no speech. That is a known, systematic, one-directional error, and
/// the right place to correct a known bias is in the prior, not by hoping a threshold absorbs it.
/// Second, a <em>usage prior</em>: a bilingual user does not dictate 50/50, and the languages they
/// have actually been using this run are more likely than the ones they have not.</para>
///
/// <para>Both are deliberately weak. The total spread between the most and least favoured candidate
/// is clamped to <see cref="MaxSpreadNats"/> — about half a nat, or 1.6:1 odds — which is enough to
/// break a tie between two windows that genuinely disagree and nowhere near enough to outvote a
/// single clean observation (worth up to ~3.4 nats). That cap is not a nicety: the usage prior is fed
/// by the app's own transcript history, so without it a wrong routing decision would raise the prior
/// for the language it wrongly chose, which would make the next wrong decision easier — a feedback
/// loop that ends with one language winning everything. Capped this low, the audio always decides and
/// the prior only ever settles ties.</para>
/// </summary>
public sealed class LanguagePrior
{
    /// <summary>
    /// Widest gap the prior may open between two candidates, in nats. 0.5 ≈ 1.65:1 odds.
    /// </summary>
    public const double MaxSpreadNats = 0.5;

    /// <summary>
    /// Penalty applied to English when the Whisper backend is answering, in nats, before clamping.
    /// ~0.45 nats halves the odds it reports for English — chosen to offset a prior that is real and
    /// well documented but modest once the candidate list is already restricted to the user's own
    /// languages, which is itself most of the mitigation.
    /// </summary>
    private const double WhisperEnglishPenalty = 0.45;

    /// <summary>
    /// Smoothing added to every language's usage count. At 2.0 a single recorded dictation barely
    /// moves the prior and it takes a consistent run to build any lean at all, which is what keeps a
    /// short-lived mistake from becoming a standing preference.
    /// </summary>
    private const double UsageSmoothing = 2.0;

    /// <summary>
    /// How strongly recency is weighted: each dictation further back counts this much less. 0.9 gives
    /// the last ten a meaningful say and the ones before that a fading one.
    /// </summary>
    private const double RecencyDecay = 0.9;

    /// <summary>Scale applied to the usage prior before the overall clamp, in nats.</summary>
    private const double UsageScale = 0.3;

    private readonly Dictionary<string, double> _logPrior = new(StringComparer.Ordinal);

    /// <summary>A prior with no opinion: every candidate starts level.</summary>
    public static LanguagePrior Uniform { get; } = new();

    /// <summary>
    /// Builds the prior for one session.
    /// </summary>
    /// <param name="candidates">The languages in play. Anything else is ignored.</param>
    /// <param name="backend">
    /// Which identifier is answering — <see cref="LanguageDetectorBackends"/> — so its known bias can
    /// be corrected.
    /// </param>
    /// <param name="recentLanguages">
    /// Languages of recent dictations, most recent first. Null or empty means no usage lean.
    /// </param>
    public static LanguagePrior Build(
        IReadOnlyList<string> candidates,
        string backend,
        IReadOnlyList<string?>? recentLanguages = null)
    {
        var prior = new LanguagePrior();
        if (candidates.Count <= 1) return prior;

        var raw = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var code = LanguageCatalog.Normalize(candidate);
            if (code.Length == 2) raw[code] = 0.0;
        }
        if (raw.Count <= 1) return prior;

        // ---- Backend bias correction ----
        if (LanguageDetectorBackends.Normalize(backend) == LanguageDetectorBackends.Whisper
            && raw.ContainsKey("en"))
        {
            raw["en"] -= WhisperEnglishPenalty;
        }

        // ---- Usage prior ----
        if (recentLanguages is { Count: > 0 })
        {
            var counts = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var code in raw.Keys) counts[code] = UsageSmoothing;

            var weight = 1.0;
            var total = UsageSmoothing * raw.Count;
            foreach (var recent in recentLanguages)
            {
                var code = LanguageCatalog.Normalize(recent);
                if (code.Length == 2 && counts.ContainsKey(code))
                {
                    counts[code] += weight;
                    total += weight;
                }
                weight *= RecencyDecay;
                if (weight < 0.01) break;   // anything this old cannot change the ordering
            }

            if (total > 0)
            {
                // Centred so the usage term shifts the candidates relative to each other rather than
                // pushing the whole set up or down, which would do nothing but is harder to read in a log.
                var mean = 0.0;
                var logs = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (code, count) in counts)
                {
                    var value = Math.Log(count / total);
                    logs[code] = value;
                    mean += value;
                }
                mean /= counts.Count;

                foreach (var (code, value) in logs) raw[code] += UsageScale * (value - mean);
            }
        }

        prior.SetClamped(raw);
        return prior;
    }

    /// <summary>
    /// Centres the accumulated nudges and squeezes their total spread into
    /// <see cref="MaxSpreadNats"/>, so no combination of corrections can add up to something that
    /// outvotes the audio.
    /// </summary>
    private void SetClamped(Dictionary<string, double> raw)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var value in raw.Values)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }

        var spread = max - min;
        var scale = spread > MaxSpreadNats ? MaxSpreadNats / spread : 1.0;
        var centre = (max + min) / 2.0;

        foreach (var (code, value) in raw) _logPrior[code] = (value - centre) * scale;
    }

    /// <summary>The log-space nudge for <paramref name="code"/>; 0 for anything not accounted for.</summary>
    public double LogPriorFor(string code) =>
        _logPrior.TryGetValue(code, out var value) ? value : 0.0;

    /// <summary>True when the prior has no opinion at all — every candidate level.</summary>
    public bool IsUniform
    {
        get
        {
            foreach (var value in _logPrior.Values)
                if (Math.Abs(value) > 1e-9) return false;
            return true;
        }
    }

    /// <summary>A compact rendering for the log: "fa +0.12, en -0.12".</summary>
    public override string ToString()
    {
        if (_logPrior.Count == 0) return "uniform";
        var parts = new List<string>(_logPrior.Count);
        foreach (var (code, value) in _logPrior) parts.Add($"{code} {value:+0.00;-0.00}");
        return string.Join(", ", parts);
    }
}
