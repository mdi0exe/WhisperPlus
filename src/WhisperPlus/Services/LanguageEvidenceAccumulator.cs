using System;
using System.Collections.Generic;
using System.Globalization;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>How far the accumulator has got with the decision.</summary>
public enum LanguageDecisionState
{
    /// <summary>Not enough to act on. Keep looking.</summary>
    Undecided,

    /// <summary>
    /// A leader worth acting on cheaply — route to it and start preloading its model — but not worth
    /// stopping the search for. This is where the latency win lives: the model loads while the
    /// evidence for it is still being gathered.
    /// </summary>
    Tentative,

    /// <summary>Decided. Stop spending inference on the question.</summary>
    Committed,
}

/// <summary>
/// What the accumulator recommends right now.
/// </summary>
/// <param name="State">How firm the recommendation is.</param>
/// <param name="Code">The language to route to, or null while nothing leads.</param>
/// <param name="Posterior">Pooled probability of <paramref name="Code"/>, for display.</param>
/// <param name="MarginNats">Lead over the runner-up in nats — what the decision actually turns on.</param>
/// <param name="Evidence">Total reliability-weighted windows pooled so far.</param>
/// <param name="Blocked">
/// True when a different language now leads but did not clear the bar for taking over from the one
/// already in use, so the recommendation deliberately stands still.
/// </param>
public readonly record struct LanguageVerdict(
    LanguageDecisionState State,
    string? Code,
    double Posterior,
    double MarginNats,
    double Evidence,
    bool Blocked)
{
    /// <summary>Nothing to act on yet.</summary>
    public static LanguageVerdict None => new(LanguageDecisionState.Undecided, null, 0, 0, 0, false);

    /// <summary>True once the question is settled and further inference is waste.</summary>
    public bool IsCommitted => State == LanguageDecisionState.Committed;

    /// <summary>True when there is a language worth routing to, however provisionally.</summary>
    public bool HasLanguage => Code is not null && State != LanguageDecisionState.Undecided;
}

/// <summary>
/// Tunables for <see cref="LanguageEvidenceAccumulator"/>. Separated so a test can state a scenario
/// in terms of the rule being tested instead of reverse-engineering it from constants, and so the
/// window-length reference can follow whichever backend is answering.
/// </summary>
public sealed record LanguageEvidenceOptions
{
    /// <summary>
    /// How much uniform noise every observation is mixed with before its log is taken:
    /// <c>p' = (1 - ε)p + ε/n</c>.
    ///
    /// <para>This is the single most important number here. Without it a softmax that saturates —
    /// and Silero's does, routinely, on a clean second of speech — contributes unbounded evidence, so
    /// one window decides the session and no amount of later disagreement can move it. At ε = 0.06 a
    /// two-way observation is worth at most ~3.4 nats no matter how certain the model claims to be,
    /// which keeps any single window arguable and lets a second one overrule a first.</para>
    /// </summary>
    public double Temper { get; init; } = 0.06;

    /// <summary>
    /// Window length that counts as a full-value look, in milliseconds. Set to the backend's
    /// <see cref="ILanguageIdentifier.MaxWindowMs"/>, since that is by definition the point past which
    /// it stops learning from more audio.
    /// </summary>
    public int ReferenceLengthMs { get; init; } = 4000;

    /// <summary>Least a window may be worth on length alone, so a short one still counts for something.</summary>
    public double MinLengthWeight { get; init; } = 0.35;

    /// <summary>In-domain mass at which a window is worth only <see cref="MassWeightFloor"/>.</summary>
    public double MassLow { get; init; } = 0.10;

    /// <summary>In-domain mass at which a window is fully trusted.</summary>
    public double MassHigh { get; init; } = 0.55;

    /// <summary>Weight retained by a window whose mass sits at <see cref="MassLow"/>.</summary>
    public double MassWeightFloor { get; init; } = 0.15;

    /// <summary>Voiced fraction at which a window is worth only <see cref="VoicedWeightFloor"/>.</summary>
    public double VoicedLow { get; init; } = 0.30;

    /// <summary>Voiced fraction at which a window is fully trusted.</summary>
    public double VoicedHigh { get; init; } = 0.70;

    /// <summary>Weight retained by a window that is mostly pause.</summary>
    public double VoicedWeightFloor { get; init; } = 0.20;

    /// <summary>
    /// How much of a window's weight is taken away by overlapping audio already pooled. At 0.8 a
    /// window that re-reads speech already seen keeps a fifth of its weight — enough to register that
    /// the model said the same thing twice, far too little to let a re-read of one second masquerade
    /// as three independent opinions, which is precisely what the old fixed probe ladder did.
    /// </summary>
    public double OverlapDiscount { get; init; } = 0.8;

    /// <summary>Lead required before routing to the leader and preloading its model, in nats (~2:1).</summary>
    public double TentativeMarginNats { get; init; } = 0.7;

    /// <summary>Pooled weight required before a tentative route is worth making.</summary>
    public double MinTentativeEvidence { get; init; } = 0.45;

    /// <summary>
    /// Lead required to accept the leader once the audio has run out, in nats (~1.3:1).
    ///
    /// <para>Lower than <see cref="TentativeMarginNats"/> on purpose, and this is the whole reason
    /// conceding is a separate step. While the user is still talking, acting on a faint lean is a bad
    /// trade: more audio is coming, and a wrong route costs a model load and every word decoded so far.
    /// Once the recording has ended that calculus inverts — nothing better is coming, and the only
    /// alternative to a faint lean is transcribing with whatever model the user happened to have
    /// configured, which carries no evidence at all. Not zero, though: a 1.05:1 lead is noise, and
    /// dressing it up as a detected language would be worse than admitting nothing was heard.</para>
    /// </summary>
    public double ConcedeMarginNats { get; init; } = 0.25;

    /// <summary>Lead required to stop looking, for two candidates, in nats (~5:1).</summary>
    public double BaseCommitMarginNats { get; init; } = 1.6;

    /// <summary>
    /// Extra lead required per doubling of the candidate count, in nats. A 3:1 lead among two
    /// languages and among eight are not the same claim, and the old fixed 0.75 probability threshold
    /// treated them identically.
    /// </summary>
    public double CandidateMarginNats { get; init; } = 0.4;

    /// <summary>
    /// Pooled weight required to stop looking. Above 1 by design: one window — however certain it
    /// sounds — can never settle the question on its own.
    /// </summary>
    public double MinCommitEvidence { get; init; } = 1.2;

    /// <summary>
    /// Extra lead a challenger must show to displace the language already in use, in nats, growing by
    /// this much again with every switch already made. Switching costs a model load and throws away
    /// every word decoded so far, so it has to be worth more than being marginally right.
    /// </summary>
    public double SwitchPenaltyNats { get; init; } = 0.9;

    /// <summary>Hard cap on route changes per session, after which the current language stands.</summary>
    public int MaxSwitches { get; init; } = 2;
}

/// <summary>
/// Decides which language is being spoken by pooling several weighted looks at the utterance, rather
/// than believing whichever one crossed a probability threshold first.
///
/// <para><b>Pooling.</b> Each observation contributes <c>w · log p_i</c> to every candidate's score,
/// where <c>w</c> is how much that window is worth (see <see cref="WeightFor"/>). This is
/// weighted log-linear pooling, not a naive-Bayes product of likelihoods, and the difference matters:
/// successive windows of one person talking are strongly correlated, and multiplying correlated
/// likelihoods is the textbook way to arrive at 99.9% confidence in the wrong answer. Weighting the
/// logs lets three glances at the same second count for about as much as one good look at it, which
/// is what they are worth.</para>
///
/// <para><b>Stopping.</b> The decision turns on the <em>margin</em> between the leader and the
/// runner-up, in nats, not on the leader's probability: with eight candidates a 40% leader beating a
/// 12% runner-up is a stronger claim than a 75% leader beating a 24% one, and the margin says so
/// while a probability threshold cannot. Committing additionally requires a floor of pooled evidence,
/// so a lucky first window cannot end the search — the failure this whole class exists to fix.</para>
///
/// <para><b>Switching.</b> Displacing a language already being transcribed with costs a model load
/// and every word decoded so far, so a challenger has to clear a wider bar, wider again each time,
/// and only so many times. That turns "the guess flickered between two models" into "it changed its
/// mind once, for a reason".</para>
///
/// <para>Not thread-safe: it belongs to one session's pump thread.</para>
/// </summary>
public sealed class LanguageEvidenceAccumulator
{
    private readonly string[] _codes;
    private readonly double[] _scores;
    private readonly LanguagePrior _prior;
    private readonly LanguageEvidenceOptions _options;

    /// <summary>Speech already looked at, for the overlap discount.</summary>
    private readonly SpanCoverage _covered = new();

    private readonly double _commitMarginNats;

    /// <param name="candidates">The languages in play; duplicates and malformed codes are dropped.</param>
    /// <param name="prior">Belief before any audio, or null for none. See <see cref="LanguagePrior"/>.</param>
    /// <param name="options">Tunables, or null for the defaults.</param>
    public LanguageEvidenceAccumulator(
        IReadOnlyList<string> candidates,
        LanguagePrior? prior = null,
        LanguageEvidenceOptions? options = null)
    {
        _prior = prior ?? LanguagePrior.Uniform;
        _options = options ?? new LanguageEvidenceOptions();

        var codes = new List<string>(candidates.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var code = LanguageCatalog.Normalize(candidate);
            if (code.Length == 2 && seen.Add(code)) codes.Add(code);
        }

        _codes = codes.ToArray();
        _scores = new double[_codes.Length];
        for (var i = 0; i < _codes.Length; i++) _scores[i] = _prior.LogPriorFor(_codes[i]);

        // More candidates, more chances for one to lead by luck — so ask for a wider lead. Measured in
        // doublings, because that is how the number of pairwise confusions grows.
        _commitMarginNats = _options.BaseCommitMarginNats + (_codes.Length > 2
            ? _options.CandidateMarginNats * Math.Log(_codes.Length / 2.0)
            : 0.0);
    }

    /// <summary>The candidates being scored, normalized and de-duplicated.</summary>
    public IReadOnlyList<string> Candidates => _codes;

    /// <summary>How many observations have been pooled, including zero-weight ones.</summary>
    public int ObservationCount { get; private set; }

    /// <summary>
    /// Total reliability-weighted evidence gathered: roughly "how many good full-length windows has
    /// this seen". The commit rule's floor is expressed in these units.
    /// </summary>
    public double Evidence { get; private set; }

    /// <summary>Lead required to stop looking, given how many candidates there are.</summary>
    public double CommitMarginNats => _commitMarginNats;

    /// <summary>Milliseconds of distinct speech that have been examined.</summary>
    public int CoveredMs => _covered.TotalMs;

    /// <summary>
    /// Pools one observation and returns the weight it was given — 0 when it said nothing usable, so a
    /// caller can log a window that cost an inference and bought nothing.
    /// </summary>
    public double Add(LanguageObservation observation)
    {
        ObservationCount++;
        if (_codes.Length < 2) return 0.0;

        var weight = WeightFor(observation);
        if (weight <= 0) return 0.0;

        var tempered = TemperedLogs(observation);
        if (tempered is null) return 0.0;

        for (var i = 0; i < _scores.Length; i++) _scores[i] += weight * tempered[i];

        Evidence += weight;
        _covered.Add(observation.StartMs, observation.EndMs);
        return weight;
    }

    /// <summary>
    /// Records that a window was examined without producing an observation — a stretch the backend
    /// declined, or one skipped as a pause. It buys no evidence, but it is audio that has been looked
    /// at, so a later window overlapping it is not as novel as it appears and the scheduler should not
    /// keep returning to it.
    /// </summary>
    public void AddExamined(int startMs, int lengthMs)
    {
        if (lengthMs <= 0) return;
        _covered.Add(startMs, startMs + lengthMs);
    }

    /// <summary>
    /// How much a window is worth, in [0, 1]. Four independent things can make a look at the audio
    /// less informative, and they multiply because any one of them being bad is enough to spoil it:
    /// it can be too short to have much in it, it can be audio the model does not recognize as any of
    /// these languages, it can be mostly pause, or it can be speech that has already been counted.
    /// </summary>
    public double WeightFor(LanguageObservation observation)
    {
        // Evidence grows with the square root of duration, not linearly: the second half of a window
        // largely confirms what the first half already showed.
        var lengthWeight = Math.Clamp(
            Math.Sqrt(observation.LengthMs / (double)Math.Max(1, _options.ReferenceLengthMs)),
            _options.MinLengthWeight,
            1.0);

        var massWeight = Ramp(
            observation.InDomainMass, _options.MassLow, _options.MassHigh, _options.MassWeightFloor);

        var voicedWeight = Ramp(
            observation.VoicedRatio, _options.VoicedLow, _options.VoicedHigh, _options.VoicedWeightFloor);

        var noveltyWeight = 1.0 - _options.OverlapDiscount
            * _covered.OverlapFraction(observation.StartMs, observation.EndMs);

        return Math.Clamp(lengthWeight * massWeight * voicedWeight * noveltyWeight, 0.0, 1.0);
    }

    /// <summary>
    /// Tempered log-probabilities, aligned with <see cref="Candidates"/>. A candidate the observation
    /// did not score at all — the backend has no class for it — is given the average of the ones it
    /// did, so a gap in the model's label set neither helps nor hurts that language. Returns null when
    /// the observation scored nothing in common with the candidate list.
    /// </summary>
    private double[]? TemperedLogs(LanguageObservation observation)
    {
        var n = _codes.Length;
        var floor = _options.Temper / n;
        var scale = 1.0 - _options.Temper;

        var logs = new double[n];
        var present = 0;
        var sumPresent = 0.0;

        for (var i = 0; i < n; i++)
        {
            var probability = observation.ProbabilityOf(_codes[i]);
            if (probability <= 0 && !ObservationScored(observation, _codes[i]))
            {
                logs[i] = double.NaN;   // filled in with the average below
                continue;
            }

            logs[i] = Math.Log(scale * probability + floor);
            present++;
            sumPresent += logs[i];
        }

        if (present == 0) return null;

        var average = sumPresent / present;
        for (var i = 0; i < n; i++) if (double.IsNaN(logs[i])) logs[i] = average;
        return logs;
    }

    private static bool ObservationScored(LanguageObservation observation, string code)
    {
        foreach (var scored in observation.Codes)
            if (string.Equals(scored, code, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Pooled probability of <paramref name="code"/>, for display and logging.</summary>
    public double PosteriorOf(string code)
    {
        var index = IndexOf(code);
        if (index < 0 || _codes.Length == 0) return 0.0;

        var max = double.NegativeInfinity;
        foreach (var score in _scores) if (score > max) max = score;

        var sum = 0.0;
        var target = 0.0;
        for (var i = 0; i < _scores.Length; i++)
        {
            var value = Math.Exp(_scores[i] - max);
            sum += value;
            if (i == index) target = value;
        }

        return sum > 0 ? target / sum : 0.0;
    }

    /// <summary>The leading candidate, or null when there is nothing to lead.</summary>
    public string? Leader => _codes.Length == 0 ? null : _codes[LeaderIndex()];

    /// <summary>The leader's lead over the runner-up, in nats. 0 with fewer than two candidates.</summary>
    public double MarginNats
    {
        get
        {
            if (_codes.Length < 2) return 0.0;
            var leader = LeaderIndex();
            var runnerUp = double.NegativeInfinity;
            for (var i = 0; i < _scores.Length; i++)
            {
                if (i == leader) continue;
                if (_scores[i] > runnerUp) runnerUp = _scores[i];
            }
            return _scores[leader] - runnerUp;
        }
    }

    /// <summary>
    /// What to do now, given what the session is already doing.
    /// </summary>
    /// <param name="applied">The language currently routed to, or null if none yet.</param>
    /// <param name="appliedCommitted">Whether <paramref name="applied"/> was a committed decision.</param>
    /// <param name="switchCount">How many times this session has already changed language.</param>
    public LanguageVerdict Evaluate(string? applied, bool appliedCommitted, int switchCount)
    {
        if (_codes.Length == 0) return LanguageVerdict.None;

        // One candidate is not a decision, it is the answer — and no audio is needed to reach it.
        if (_codes.Length == 1)
            return new LanguageVerdict(LanguageDecisionState.Committed, _codes[0], 1.0, double.PositiveInfinity,
                Evidence, false);

        var leaderIndex = LeaderIndex();
        var leader = _codes[leaderIndex];
        var margin = MarginNats;

        var state = LanguageDecisionState.Undecided;
        if (margin >= _commitMarginNats && Evidence >= _options.MinCommitEvidence)
            state = LanguageDecisionState.Committed;
        else if (margin >= _options.TentativeMarginNats && Evidence >= _options.MinTentativeEvidence)
            state = LanguageDecisionState.Tentative;

        var appliedIndex = applied is null ? -1 : IndexOf(applied);

        // Nothing to displace: the leader stands on its own merits.
        if (appliedIndex < 0 || appliedIndex == leaderIndex)
            return new LanguageVerdict(state, leader, PosteriorOf(leader), margin, Evidence, false);

        // A different language leads. Taking over from one already being transcribed with costs a model
        // load and every word decoded so far, so it must clear a wider bar — wider again with each
        // switch already spent, and only so many times.
        var required = (appliedCommitted ? _commitMarginNats : _options.TentativeMarginNats)
                       + _options.SwitchPenaltyNats * (1 + switchCount);
        var lead = _scores[leaderIndex] - _scores[appliedIndex];

        if (switchCount >= _options.MaxSwitches || lead < required)
        {
            // Stand still. A committed decision that survives a challenge stays committed; a tentative
            // one keeps looking, since the challenge is itself a reason to keep looking.
            var standing = appliedCommitted ? LanguageDecisionState.Committed : LanguageDecisionState.Tentative;
            return new LanguageVerdict(
                standing, applied, PosteriorOf(applied!), margin, Evidence, true);
        }

        return new LanguageVerdict(state, leader, PosteriorOf(leader), margin, Evidence, false);
    }

    /// <summary>
    /// The best answer available when there is no more audio to look at: the leader if it is at least
    /// arguable, otherwise nothing. Used at the end of a session, where "a likely language" beats
    /// "no language" — the alternative is transcribing with a model chosen by default.
    /// </summary>
    public LanguageVerdict Concede(string? applied, bool appliedCommitted, int switchCount)
    {
        var verdict = Evaluate(applied, appliedCommitted, switchCount);
        if (verdict.State != LanguageDecisionState.Undecided || _codes.Length < 2) return verdict;

        var leader = Leader;
        if (leader is null || Evidence <= 0) return verdict;
        if (MarginNats < _options.ConcedeMarginNats) return verdict;

        return verdict with { State = LanguageDecisionState.Tentative, Code = leader, Blocked = false };
    }

    private int LeaderIndex()
    {
        var best = 0;
        for (var i = 1; i < _scores.Length; i++) if (_scores[i] > _scores[best]) best = i;
        return best;
    }

    private int IndexOf(string code)
    {
        for (var i = 0; i < _codes.Length; i++)
            if (string.Equals(_codes[i], code, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// Smooth ramp from <paramref name="floor"/> at <paramref name="low"/> to 1 at
    /// <paramref name="high"/>. Smooth rather than linear so a value sitting near a boundary does not
    /// make the weight — and through it the decision — hinge on a hair's difference in level.
    /// </summary>
    private static double Ramp(double value, double low, double high, double floor)
    {
        if (high <= low) return value >= high ? 1.0 : floor;

        var t = Math.Clamp((value - low) / (high - low), 0.0, 1.0);
        var smooth = t * t * (3.0 - 2.0 * t);
        return floor + (1.0 - floor) * smooth;
    }

    /// <summary>One line describing the current state of the decision, for the log.</summary>
    public string Describe()
    {
        var leader = Leader;
        if (leader is null) return "no candidates";

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} p={1:P0} margin={2:0.00}/{3:0.00} nats evidence={4:0.00} windows={5} covered={6} ms",
            leader, PosteriorOf(leader), MarginNats, _commitMarginNats, Evidence, ObservationCount, CoveredMs);
    }
}
