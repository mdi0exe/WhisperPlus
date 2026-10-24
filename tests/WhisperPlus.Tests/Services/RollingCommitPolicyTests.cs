using System;
using System.Collections.Generic;
using WhisperPlus.Services;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for the pure rolling-commit policy that decides which Whisper segments have settled. The
/// policy is the part of live transcription that has to be exactly right with no model in the
/// loop: it governs which audio is never re-decoded and which text is shown live, so every
/// branch — lag window, ordering, final flush, empty input — is covered here.
/// </summary>
public class RollingCommitPolicyTests
{
    private const int SampleRate = 16000;

    private static TranscribedSegment Seg(string text, double startS, double endS) =>
        new(text, TimeSpan.FromSeconds(startS), TimeSpan.FromSeconds(endS));

    // samples <-> milliseconds helper so the assertions read in the same units the code uses.
    private static int MsToSamples(int ms) => ms * SampleRate / 1000;

    [Fact]
    public void Empty_Input_Is_NoOp()
    {
        var result = RollingCommitPolicy.Split(
            Array.Empty<TranscribedSegment>(), windowSamples: MsToSamples(2000));

        Assert.Equal("", result.CommittedText);
        Assert.Equal("", result.PreviewText);
        Assert.Equal(0, result.CommittedSamples);
    }

    [Fact]
    public void Null_Input_Is_NoOp()
    {
        var result = RollingCommitPolicy.Split(null!, windowSamples: MsToSamples(2000));

        Assert.Equal(0, result.CommittedSamples);
    }

    /// <summary>
    /// The headline guarantee: a segment ending inside the lag window is NOT committed, because
    /// Whisper reconsiders it once more audio arrives. With the default 1.5 s lag, a segment that
    /// ends at 0.5 s inside a 2 s window is too fresh and goes to preview only.
    /// </summary>
    [Fact]
    public void Segment_Ending_Inside_Lag_Window_Is_Not_Committed()
    {
        // 2 s window, 1.5 s lag → cutoff = 500 ms. A segment ending at 500 ms is at the boundary;
        // one ending at 600 ms is past it and must stay in preview.
        var segments = new[]
        {
            Seg("hello", 0.0, 0.5),
            Seg("world", 0.5, 0.6),
        };

        var result = RollingCommitPolicy.Split(segments, windowSamples: MsToSamples(2000));

        Assert.Equal("hello", result.CommittedText);
        Assert.Equal("world", result.PreviewText);
        Assert.True(result.CommittedSamples > 0);
    }

    [Fact]
    public void Old_Segments_Are_Committed_In_Order()
    {
        // 4 s window, 1.5 s lag → cutoff = 2500 ms. Both early segments settled, last one didn't.
        var segments = new[]
        {
            Seg("one",   0.0, 1.0),
            Seg("two",   1.0, 2.0),
            Seg("three", 2.0, 2.6),
        };

        var result = RollingCommitPolicy.Split(segments, windowSamples: MsToSamples(4000));

        Assert.Equal("one two", result.CommittedText);
        Assert.Equal("three", result.PreviewText);
        // Committed samples track the end of the last committed segment (2.0 s), capped at window.
        Assert.Equal(MsToSamples(2000), result.CommittedSamples);
    }

    /// <summary>
    /// Segments arrive in order, so once one is too recent to commit every later one is too — even
    /// if a stray timestamp would place a later segment before the cutoff. This keeps the boundary
    /// monotonic and matches how Whisper actually emits timestamps.
    /// </summary>
    [Fact]
    public void Once_Settling_All_Later_Segments_Are_Preview()
    {
        var segments = new[]
        {
            Seg("settled", 0.0, 0.4),
            Seg("fresh",   0.4, 0.7),   // past cutoff (500 ms) → preview starts
            Seg("weird",   0.0, 0.1),   // stray early timestamp — still preview, not committed
        };

        var result = RollingCommitPolicy.Split(segments, windowSamples: MsToSamples(2000));

        Assert.Equal("settled", result.CommittedText);
        Assert.Equal("fresh weird", result.PreviewText);
    }

    /// <summary>
    /// On finish, there is no more audio coming, so nothing can be revised: every segment commits.
    /// Committed samples track the end of the last segment (not the window length) — audio the
    /// window held but no segment covered is never claimed as consumed.
    /// </summary>
    [Fact]
    public void Final_Flush_Commits_Everything()
    {
        var segments = new[]
        {
            Seg("one",   0.0, 0.6),
            Seg("two",   0.6, 0.9),
        };
        var window = MsToSamples(1000);

        var result = RollingCommitPolicy.Split(segments, windowSamples: window, final: true);

        Assert.Equal("one two", result.CommittedText);
        Assert.Equal("", result.PreviewText);
        Assert.Equal(MsToSamples(900), result.CommittedSamples);
    }

    [Fact]
    public void Nothing_Committed_Advances_Zero_Samples()
    {
        // Whole window is inside the lag window → nothing settles, origin must not advance.
        var segments = new[] { Seg("still talking", 0.0, 0.8) };

        var result = RollingCommitPolicy.Split(segments, windowSamples: MsToSamples(1000));

        Assert.Equal("", result.CommittedText);
        Assert.Equal("still talking", result.PreviewText);
        Assert.Equal(0, result.CommittedSamples);
    }

    /// <summary>
    /// A segment whose end timestamp overshoots the actual window is committed (on flush), but the
    /// policy caps committed samples at the window length so the caller never advances past audio
    /// that was never there. With a 2 s window and a 3 s segment, the 3 s end would claim 48000
    /// samples but the window only held 32000 — the cap takes the smaller.
    /// </summary>
    [Fact]
    public void Committed_Samples_Capped_At_Window()
    {
        var segments = new[] { Seg("overflow", 0.0, 3.0) };
        var window = MsToSamples(2000);   // smaller than the segment's end

        var result = RollingCommitPolicy.Split(segments, windowSamples: window, final: true);

        Assert.Equal("overflow", result.CommittedText);
        Assert.Equal(window, result.CommittedSamples);   // clamped, not 3 s worth
    }

    [Fact]
    public void Empty_Text_Segments_Are_Skipped()
    {
        // 4 s window, 1.5 s lag → cutoff 2500 ms. All three segments are well before the cutoff, so
        // the empty/whitespace ones are dropped and only "real" commits.
        var segments = new[]
        {
            Seg("",     0.0, 0.4),
            Seg("   ",  0.4, 0.5),
            Seg("real", 0.5, 0.9),
        };

        var result = RollingCommitPolicy.Split(segments, windowSamples: MsToSamples(4000));

        Assert.Equal("real", result.CommittedText);
    }
}
