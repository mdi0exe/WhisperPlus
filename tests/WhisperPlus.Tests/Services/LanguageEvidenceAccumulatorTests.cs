using System;
using System.Collections.Generic;
using WhisperPlus.Models;
using WhisperPlus.Services;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for the language decision rule — the part that used to be a single
/// <c>if (probability >= 0.75) settled = true</c> and is now weighted evidence pooling.
///
/// <para>Each test states the property it is defending rather than a magic number, so the constants in
/// <see cref="LanguageEvidenceOptions"/> can be retuned without rewriting the suite. The bugs being
/// guarded against are specific and were all real: one window deciding a session, three re-reads of the
/// same second counting as three opinions, a fixed probability threshold that ignored how many
/// languages were in play, silence scoring as a confident answer, and the route flickering between two
/// models.</para>
/// </summary>
public class LanguageEvidenceAccumulatorTests
{
    private static readonly string[] TwoWay = { "fa", "en" };

    /// <summary>A full-length, fully voiced, entirely in-domain window — the best case.</summary>
    private static LanguageObservation Clean(
        double faProbability, int startMs = 0, int lengthMs = 4000, double mass = 0.95)
    {
        return new LanguageObservation(
            TwoWay,
            new[] { faProbability, 1.0 - faProbability },
            mass, startMs, lengthMs, voicedRatio: 1.0);
    }

    // ---- The headline fix: one window is never a decision ----

    [Fact]
    public void One_Saturated_Window_Does_Not_Commit()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);

        // As certain as a softmax can plausibly get. Under the old rule this ended the search.
        accumulator.Add(Clean(0.999));

        var verdict = accumulator.Evaluate(applied: null, appliedCommitted: false, switchCount: 0);

        Assert.Equal("fa", verdict.Code);
        Assert.False(verdict.IsCommitted);

        // It is still acted on immediately — the model preload must not wait for proof.
        Assert.Equal(LanguageDecisionState.Tentative, verdict.State);
    }

    [Fact]
    public void Two_Agreeing_Windows_Commit()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);

        accumulator.Add(Clean(0.95, startMs: 0));
        accumulator.Add(Clean(0.95, startMs: 4000));   // different audio, so it counts in full

        var verdict = accumulator.Evaluate(applied: "fa", appliedCommitted: false, switchCount: 0);

        Assert.Equal("fa", verdict.Code);
        Assert.True(verdict.IsCommitted);
    }

    [Fact]
    public void Tempering_Bounds_What_One_Window_Can_Claim()
    {
        var certain = new LanguageEvidenceAccumulator(TwoWay);
        certain.Add(Clean(0.999999));

        var confident = new LanguageEvidenceAccumulator(TwoWay);
        confident.Add(Clean(0.97));

        // A model claiming near-certainty is allowed to be more convincing than one claiming 97%, but
        // only by a little: without tempering the first margin would be ~14 nats against ~3.5, and one
        // window would be unanswerable by any amount of later disagreement.
        Assert.True(certain.MarginNats > confident.MarginNats);
        Assert.True(certain.MarginNats < 2 * confident.MarginNats);
    }

    // ---- Correlated re-reads ----

    [Fact]
    public void Asking_The_Same_Audio_Repeatedly_Manufactures_No_Evidence()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);

        // The same second of speech, three times. A model asked the same question three times gives
        // the same answer three times; that is one opinion, not three, and no number of repeats of it
        // should be able to settle a session.
        for (var i = 0; i < 3; i++) accumulator.Add(Clean(0.9, startMs: 0, lengthMs: 1000));

        var verdict = accumulator.Evaluate(applied: "fa", appliedCommitted: false, switchCount: 0);

        Assert.False(verdict.IsCommitted);
    }

    [Fact]
    public void A_Growing_Window_Is_Credited_Only_For_What_Is_New()
    {
        // The old probe ladder's first three rungs: 0-1000, 0-2000, 0-3500. Each does add speech
        // nobody has seen, so 3.5 s of consistent evidence is allowed to settle the question — but on
        // the strength of the audio it actually covers, not of three separate opinions.
        var growing = new LanguageEvidenceAccumulator(TwoWay);
        growing.Add(Clean(0.9, startMs: 0, lengthMs: 1000));
        growing.Add(Clean(0.9, startMs: 0, lengthMs: 2000));
        growing.Add(Clean(0.9, startMs: 0, lengthMs: 3500));

        var disjoint = new LanguageEvidenceAccumulator(TwoWay);
        disjoint.Add(Clean(0.9, startMs: 0, lengthMs: 1000));
        disjoint.Add(Clean(0.9, startMs: 1000, lengthMs: 2000));
        disjoint.Add(Clean(0.9, startMs: 3000, lengthMs: 3500));

        Assert.True(growing.Evidence < disjoint.Evidence);
        Assert.Equal(3500, growing.CoveredMs);
    }

    [Fact]
    public void Fresh_Audio_Is_Worth_More_Than_A_Re_Read()
    {
        var fresh = new LanguageEvidenceAccumulator(TwoWay);
        fresh.Add(Clean(0.9, startMs: 0));
        fresh.Add(Clean(0.9, startMs: 4000));

        var reread = new LanguageEvidenceAccumulator(TwoWay);
        reread.Add(Clean(0.9, startMs: 0));
        reread.Add(Clean(0.9, startMs: 0));

        Assert.True(fresh.Evidence > reread.Evidence);
        Assert.True(fresh.MarginNats > reread.MarginNats);
    }

    [Fact]
    public void Examined_Audio_Discounts_A_Later_Overlapping_Window()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);

        // Declined and pause-skipped windows still count as looked-at.
        accumulator.AddExamined(0, 4000);
        var weight = accumulator.Add(Clean(0.9, startMs: 0, lengthMs: 4000));

        Assert.True(weight > 0);        // it says something
        Assert.True(weight < 0.35);     // but not what a fresh window would say
    }

    // ---- Evidence quality ----

    [Fact]
    public void Low_In_Domain_Mass_Counts_For_Little()
    {
        var strong = new LanguageEvidenceAccumulator(TwoWay);
        strong.Add(Clean(0.9, mass: 0.95));

        var marginal = new LanguageEvidenceAccumulator(TwoWay);
        marginal.Add(Clean(0.9, mass: 0.11));   // barely past Silero's decline gate

        // Under the old code these were identical: the mass was a yes/no gate and then discarded.
        Assert.True(marginal.Evidence < strong.Evidence / 3);
        Assert.True(marginal.MarginNats < strong.MarginNats / 3);
    }

    [Fact]
    public void A_Mostly_Pause_Window_Counts_For_Little()
    {
        var voiced = new LanguageEvidenceAccumulator(TwoWay);
        voiced.Add(new LanguageObservation(TwoWay, new[] { 0.9, 0.1 }, 0.95, 0, 4000, voicedRatio: 1.0));

        var mostlySilence = new LanguageEvidenceAccumulator(TwoWay);
        mostlySilence.Add(new LanguageObservation(TwoWay, new[] { 0.9, 0.1 }, 0.95, 0, 4000, voicedRatio: 0.15));

        Assert.True(mostlySilence.Evidence < voiced.Evidence / 2);
    }

    [Fact]
    public void A_Short_Window_Counts_For_Less_Than_A_Long_One()
    {
        var options = new LanguageEvidenceOptions { ReferenceLengthMs = 4000 };

        var brief = new LanguageEvidenceAccumulator(TwoWay, null, options);
        brief.Add(Clean(0.9, lengthMs: 1000));

        var full = new LanguageEvidenceAccumulator(TwoWay, null, options);
        full.Add(Clean(0.9, lengthMs: 4000));

        Assert.True(brief.Evidence < full.Evidence);
        Assert.True(brief.Evidence > 0);   // but it is not worthless
    }

    // ---- Candidate count ----

    [Fact]
    public void More_Candidates_Demand_A_Wider_Margin()
    {
        var two = new LanguageEvidenceAccumulator(new[] { "fa", "en" });
        var six = new LanguageEvidenceAccumulator(new[] { "fa", "en", "de", "fr", "es", "ar" });

        // A 3:1 lead over the runner-up among two languages and among six are not the same claim, and
        // the old single probability threshold treated them identically.
        Assert.True(six.CommitMarginNats > two.CommitMarginNats);
    }

    [Fact]
    public void Disagreement_Between_Windows_Prevents_A_Commit()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);

        accumulator.Add(Clean(0.95, startMs: 0));
        accumulator.Add(Clean(0.05, startMs: 4000));   // equally confident the other way

        var verdict = accumulator.Evaluate(applied: null, appliedCommitted: false, switchCount: 0);

        Assert.False(verdict.IsCommitted);
        Assert.Equal(LanguageDecisionState.Undecided, verdict.State);
    }

    [Fact]
    public void A_Second_Window_Can_Overturn_A_Tentative_First()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);

        accumulator.Add(Clean(0.85, startMs: 0, lengthMs: 1200));
        var early = accumulator.Evaluate(null, false, 0);
        Assert.Equal("fa", early.Code);

        // Three clean full-length looks the other way, from audio never seen before. This is exactly
        // what the old rule could not do: once settled, nothing could change its mind.
        accumulator.Add(Clean(0.02, startMs: 2000));
        accumulator.Add(Clean(0.02, startMs: 6000));
        accumulator.Add(Clean(0.02, startMs: 10000));

        var later = accumulator.Evaluate("fa", appliedCommitted: false, switchCount: 0);
        Assert.Equal("en", later.Code);
    }

    // ---- Hysteresis ----

    [Fact]
    public void A_Narrow_Lead_Does_Not_Displace_The_Applied_Language()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);

        accumulator.Add(Clean(0.45, startMs: 0));       // "en" leads, barely

        var verdict = accumulator.Evaluate(applied: "fa", appliedCommitted: false, switchCount: 0);

        // Switching costs a model load and every word decoded so far, so a hair's lead is not enough.
        Assert.Equal("fa", verdict.Code);
        Assert.True(verdict.Blocked);
    }

    [Fact]
    public void Each_Switch_Makes_The_Next_One_Harder()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);
        accumulator.Add(Clean(0.02, startMs: 0));
        accumulator.Add(Clean(0.02, startMs: 4000));

        // The same evidence that justifies a first switch need not justify a third.
        Assert.Equal("en", accumulator.Evaluate("fa", false, switchCount: 0).Code);
        Assert.Equal("fa", accumulator.Evaluate("fa", false, switchCount: 5).Code);
    }

    [Fact]
    public void Switching_Stops_After_The_Cap()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);
        for (var i = 0; i < 6; i++) accumulator.Add(Clean(0.001, startMs: i * 4000));

        var verdict = accumulator.Evaluate("fa", appliedCommitted: false, switchCount: 2);

        // Overwhelming evidence, but the session has already flip-flopped twice: stop. Two model
        // reloads mid-dictation is the most a user should ever be made to sit through.
        Assert.Equal("fa", verdict.Code);
        Assert.True(verdict.Blocked);
    }

    [Fact]
    public void A_Blocked_Committed_Language_Stays_Committed()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);
        accumulator.Add(Clean(0.4, startMs: 0));

        var verdict = accumulator.Evaluate("fa", appliedCommitted: true, switchCount: 0);

        Assert.Equal("fa", verdict.Code);
        Assert.True(verdict.IsCommitted);
    }

    // ---- Conceding at the end of a session ----

    [Fact]
    public void Concede_Takes_A_Weak_Leader_Rather_Than_Nothing()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);

        // One brief, half-voiced look — not enough for a tentative route while there is still audio to
        // come, since acting on it costs a model load that more audio might prove wasted.
        accumulator.Add(new LanguageObservation(TwoWay, new[] { 0.82, 0.18 }, 0.5, 0, 1000, 0.5));
        Assert.Equal(LanguageDecisionState.Undecided,
            accumulator.Evaluate(null, false, 0).State);

        // With the recording over the alternative is transcribing with a model chosen by default, so a
        // likely language wins.
        var conceded = accumulator.Concede(null, false, 0);
        Assert.Equal("fa", conceded.Code);
        Assert.True(conceded.HasLanguage);
    }

    [Fact]
    public void Concede_Reports_Nothing_When_The_Evidence_Is_Split()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);
        accumulator.Add(Clean(0.5, startMs: 0));

        var conceded = accumulator.Concede(null, false, 0);

        Assert.False(conceded.HasLanguage);
    }

    [Fact]
    public void Concede_Reports_Nothing_When_No_Window_Ever_Answered()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);
        accumulator.AddExamined(0, 8000);   // all declined or all pause

        var conceded = accumulator.Concede(null, false, 0);

        Assert.False(conceded.HasLanguage);
        Assert.Equal(0, accumulator.Evidence);
    }

    // ---- Degenerate inputs ----

    [Fact]
    public void A_Single_Candidate_Needs_No_Audio()
    {
        var accumulator = new LanguageEvidenceAccumulator(new[] { "fa" });

        var verdict = accumulator.Evaluate(null, false, 0);

        Assert.Equal("fa", verdict.Code);
        Assert.True(verdict.IsCommitted);
    }

    [Fact]
    public void No_Candidates_Decides_Nothing()
    {
        var accumulator = new LanguageEvidenceAccumulator(Array.Empty<string>());

        Assert.Null(accumulator.Evaluate(null, false, 0).Code);
        Assert.Equal(0, accumulator.Add(Clean(0.9)));
    }

    [Fact]
    public void Candidates_Are_Normalized_And_Deduplicated()
    {
        var accumulator = new LanguageEvidenceAccumulator(new[] { "FA", " fa ", "en", "zzz", "" });

        Assert.Equal(new[] { "fa", "en" }, accumulator.Candidates);
    }

    [Fact]
    public void A_Candidate_The_Model_Cannot_Score_Is_Neither_Helped_Nor_Hurt()
    {
        // Silero has no class for some languages, so an observation can omit a candidate entirely.
        var accumulator = new LanguageEvidenceAccumulator(new[] { "fa", "en", "de" });
        accumulator.Add(new LanguageObservation(
            new[] { "fa", "en" }, new[] { 0.5, 0.5 }, 0.9, 0, 4000, 1.0));

        // A gap in the model's label set must not read as evidence against that language, nor for it.
        Assert.Equal(accumulator.PosteriorOf("fa"), accumulator.PosteriorOf("de"), 6);
        Assert.Equal(accumulator.PosteriorOf("en"), accumulator.PosteriorOf("de"), 6);
    }

    [Fact]
    public void Posteriors_Sum_To_One()
    {
        var accumulator = new LanguageEvidenceAccumulator(new[] { "fa", "en", "de" });
        accumulator.Add(new LanguageObservation(
            new[] { "fa", "en", "de" }, new[] { 0.6, 0.3, 0.1 }, 0.9, 0, 4000, 1.0));

        var total = accumulator.PosteriorOf("fa") + accumulator.PosteriorOf("en") + accumulator.PosteriorOf("de");
        Assert.Equal(1.0, total, 6);
    }

    [Fact]
    public void Coverage_Tracks_Distinct_Audio_Only()
    {
        var accumulator = new LanguageEvidenceAccumulator(TwoWay);

        accumulator.Add(Clean(0.9, startMs: 0, lengthMs: 2000));
        accumulator.Add(Clean(0.9, startMs: 1000, lengthMs: 2000));   // overlaps the first

        Assert.Equal(3000, accumulator.CoveredMs);
    }

    // ---- The prior ----

    [Fact]
    public void The_Prior_Breaks_A_Tie_But_Cannot_Beat_The_Audio()
    {
        var prior = LanguagePrior.Build(TwoWay, LanguageDetectorBackends.Silero,
            new[] { "fa", "fa", "fa", "fa", "fa", "fa", "fa", "fa" });

        var tied = new LanguageEvidenceAccumulator(TwoWay, prior);
        tied.Add(Clean(0.5));
        Assert.Equal("fa", tied.Leader);       // history breaks the tie

        var contradicted = new LanguageEvidenceAccumulator(TwoWay, prior);
        contradicted.Add(Clean(0.25));         // even a mild lean in the audio overrules it
        Assert.Equal("en", contradicted.Leader);
    }

    [Fact]
    public void Whisper_Gets_Its_English_Prior_Corrected()
    {
        var silero = LanguagePrior.Build(TwoWay, LanguageDetectorBackends.Silero);
        var whisper = LanguagePrior.Build(TwoWay, LanguageDetectorBackends.Whisper);

        Assert.True(silero.IsUniform);
        Assert.True(whisper.LogPriorFor("en") < whisper.LogPriorFor("fa"));
    }

    [Fact]
    public void The_Prior_Is_Clamped_However_Lopsided_The_History()
    {
        var history = new List<string?>();
        for (var i = 0; i < 200; i++) history.Add("fa");

        var prior = LanguagePrior.Build(TwoWay, LanguageDetectorBackends.Whisper, history);
        var spread = prior.LogPriorFor("fa") - prior.LogPriorFor("en");

        // The clamp is what stops history → prior → routing → history from becoming a loop that ends
        // with one language winning every session regardless of what was said.
        Assert.True(spread <= LanguagePrior.MaxSpreadNats + 1e-9);
    }

    [Fact]
    public void A_Single_Candidate_Gets_No_Prior_At_All()
    {
        var prior = LanguagePrior.Build(new[] { "en" }, LanguageDetectorBackends.Whisper,
            new[] { "fa", "fa" });

        Assert.True(prior.IsUniform);
    }
}
