using System.Collections.Generic;
using System.Linq;
using WhisperPlus.Services;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for which audio gets examined, and for the coverage arithmetic underneath it.
///
/// <para>The scheduler replaced a fixed table of offsets whose first three entries were the same
/// speech read three times. What is defended here is the property that made that table wrong: a
/// second look must come from somewhere the first one did not.</para>
/// </summary>
public class LanguageProbeSchedulerTests
{
    private static LanguageProbeScheduler Scheduler(int maxWindows = 10) =>
        new(firstWindowMs: 1200, windowMs: 4000, minWindowMs: 1000, maxWindows: maxWindows);

    /// <summary>Runs the schedule to exhaustion over a fixed amount of speech.</summary>
    private static List<ProbeWindow> DrainAll(
        LanguageProbeScheduler scheduler, int availableMs, bool allowOverlap = true)
    {
        var windows = new List<ProbeWindow>();
        while (scheduler.TryNext(availableMs, allowOverlap, out var window))
        {
            windows.Add(window);
            scheduler.MarkExamined(window);
        }
        return windows;
    }

    // ---- Latency ----

    [Fact]
    public void Waits_Until_There_Is_Enough_Speech_To_Ask_About()
    {
        var scheduler = Scheduler();

        Assert.False(scheduler.TryNext(availableMs: 400, allowOverlap: false, out _));
        Assert.Equal(0, scheduler.WindowsIssued);
    }

    [Fact]
    public void The_First_Window_Comes_As_Soon_As_A_Usable_Amount_Exists()
    {
        var scheduler = Scheduler();

        Assert.True(scheduler.TryNext(availableMs: 1000, allowOverlap: false, out var window));
        Assert.Equal(0, window.StartMs);

        // The whole point of the short first window: nothing can be routed, and no model can start
        // loading, until it answers.
        Assert.Equal(1000, window.LengthMs);
    }

    [Fact]
    public void Later_Windows_Are_As_Long_As_The_Backend_Can_Use()
    {
        var scheduler = Scheduler();

        scheduler.TryNext(20000, false, out var first);
        scheduler.MarkExamined(first);
        scheduler.TryNext(20000, false, out var second);

        Assert.Equal(1200, first.LengthMs);
        Assert.Equal(4000, second.LengthMs);
    }

    // ---- The headline property ----

    [Fact]
    public void A_Second_Look_Comes_From_Audio_The_First_Did_Not_Cover()
    {
        var scheduler = Scheduler();
        var windows = DrainAll(scheduler, availableMs: 20000, allowOverlap: false);

        Assert.True(windows.Count > 1);
        for (var i = 1; i < windows.Count; i++)
        {
            // Each window begins where the examined audio ended: no re-reads while the user is talking.
            Assert.Equal(windows[i - 1].EndMs, windows[i].StartMs);
            Assert.Equal(windows[i].LengthMs, windows[i].NovelMs);
        }
    }

    [Fact]
    public void Examined_Audio_Is_Never_Offered_Again_While_Fresh_Audio_Remains()
    {
        var scheduler = Scheduler();
        var windows = DrainAll(scheduler, availableMs: 20000, allowOverlap: false);

        var covered = new HashSet<int>();
        foreach (var window in windows)
        {
            for (var ms = window.StartMs; ms < window.EndMs; ms += 100)
                Assert.True(covered.Add(ms), $"{ms} ms was examined twice.");
        }
    }

    [Fact]
    public void Coverage_Advances_Across_The_Whole_Utterance()
    {
        var scheduler = Scheduler();
        DrainAll(scheduler, availableMs: 12000, allowOverlap: true);

        // 12 s of speech, examined end to end — the old ladder stopped looking after 12 s only because
        // it ran out of table entries, and stopped at 3.5 s if the early rungs answered.
        Assert.True(scheduler.Examined.TotalMs >= 11000);
    }

    // ---- Overlap, and when it is allowed ----

    [Fact]
    public void A_Short_Tail_Is_Skipped_While_The_User_Is_Still_Talking()
    {
        var scheduler = Scheduler();
        scheduler.MarkExamined(new ProbeWindow(0, 5000, 5000));

        // 600 ms of new speech is not enough for a window on its own, and more is on the way, so
        // waiting beats paying for a mostly-re-read window now.
        Assert.False(scheduler.TryNext(availableMs: 5600, allowOverlap: false, out _));
    }

    [Fact]
    public void A_Short_Tail_Is_Reached_For_Once_The_Audio_Has_Ended()
    {
        var scheduler = Scheduler();
        scheduler.MarkExamined(new ProbeWindow(0, 5000, 5000));

        // Nothing more is coming, so a window that reaches back over examined speech to pick up the
        // tail is the best offer left. It is honest about how little of it is new.
        Assert.True(scheduler.TryNext(availableMs: 5600, allowOverlap: true, out var window));
        Assert.Equal(5600, window.EndMs);
        Assert.True(window.LengthMs >= 1000);

        // Only the 600 ms nobody has heard is new; the accumulator prices the window accordingly
        // rather than counting a re-read as a fresh opinion.
        Assert.Equal(600, window.NovelMs);
        Assert.True(window.NovelMs < window.LengthMs);
    }

    [Fact]
    public void A_Sliver_Of_New_Audio_Is_Not_Worth_A_Window()
    {
        var scheduler = Scheduler();
        scheduler.MarkExamined(new ProbeWindow(0, 5000, 5000));

        Assert.False(scheduler.TryNext(availableMs: 5100, allowOverlap: true, out _));
    }

    [Fact]
    public void Gaps_Left_By_Skipped_Windows_Are_Not_Revisited()
    {
        var scheduler = Scheduler();

        // A window rejected as a pause is still marked examined: the schedule must move on rather than
        // ask about the same silence again.
        scheduler.TryNext(20000, false, out var first);
        scheduler.MarkExamined(first);
        scheduler.TryNext(20000, false, out var second);
        scheduler.MarkExamined(second);

        Assert.Equal(first.EndMs, second.StartMs);
        Assert.Equal(second.EndMs, scheduler.Examined.EndMs);
    }

    // ---- Bounds ----

    [Fact]
    public void The_Window_Budget_Is_Respected()
    {
        var scheduler = Scheduler(maxWindows: 3);
        var windows = DrainAll(scheduler, availableMs: 120000);

        Assert.Equal(3, windows.Count);
        Assert.True(scheduler.IsExhausted);
    }

    [Fact]
    public void A_Fully_Examined_Utterance_Offers_Nothing_More()
    {
        var scheduler = Scheduler();
        DrainAll(scheduler, availableMs: 6000);

        Assert.False(scheduler.TryNext(6000, allowOverlap: true, out _));
    }

    [Fact]
    public void Windows_Never_Run_Past_The_Available_Audio()
    {
        var scheduler = Scheduler();

        foreach (var window in DrainAll(scheduler, availableMs: 9500))
            Assert.True(window.EndMs <= 9500, $"window ended at {window.EndMs} ms, past the audio.");
    }
}

/// <summary>
/// Tests for the interval bookkeeping both the scheduler and the accumulator lean on: one to find
/// audio nobody has looked at, the other to discount audio everybody has.
/// </summary>
public class SpanCoverageTests
{
    [Fact]
    public void Overlapping_Spans_Merge()
    {
        var coverage = new SpanCoverage();
        coverage.Add(0, 1000);
        coverage.Add(500, 1500);

        Assert.Single(coverage.Spans);
        Assert.Equal(1500, coverage.TotalMs);
    }

    [Fact]
    public void Touching_Spans_Merge()
    {
        var coverage = new SpanCoverage();
        coverage.Add(0, 1000);
        coverage.Add(1000, 2000);

        Assert.Single(coverage.Spans);
        Assert.Equal(2000, coverage.EndMs);
    }

    [Fact]
    public void Disjoint_Spans_Stay_Separate_And_Sorted()
    {
        var coverage = new SpanCoverage();
        coverage.Add(4000, 5000);
        coverage.Add(0, 1000);

        Assert.Equal(2, coverage.Spans.Count);
        Assert.Equal((0, 1000), coverage.Spans[0]);
        Assert.Equal((4000, 5000), coverage.Spans[1]);
        Assert.Equal(2000, coverage.TotalMs);
    }

    [Fact]
    public void Empty_And_Backwards_Spans_Are_Ignored()
    {
        var coverage = new SpanCoverage();
        coverage.Add(1000, 1000);
        coverage.Add(2000, 1000);

        Assert.True(coverage.IsEmpty);
    }

    [Theory]
    [InlineData(0, 1000, 1000)]     // wholly covered
    [InlineData(500, 1500, 500)]    // half covered
    [InlineData(1000, 2000, 0)]     // adjacent, not overlapping
    [InlineData(-500, 500, 500)]    // reaching in from before the start
    public void Overlap_Is_Measured_Exactly(int start, int end, int expected)
    {
        var coverage = new SpanCoverage();
        coverage.Add(0, 1000);

        Assert.Equal(expected, coverage.OverlapWith(start, end));
    }

    [Fact]
    public void Overlap_Fraction_Handles_An_Empty_Window()
    {
        var coverage = new SpanCoverage();
        coverage.Add(0, 1000);

        Assert.Equal(0.0, coverage.OverlapFraction(500, 500));
    }

    [Fact]
    public void Gaps_Are_The_Parts_Not_Yet_Covered()
    {
        var coverage = new SpanCoverage();
        coverage.Add(1000, 2000);
        coverage.Add(4000, 5000);

        var gaps = coverage.GapsWithin(7000).ToList();

        Assert.Equal(new[] { (0, 1000), (2000, 4000), (5000, 7000) }, gaps);
    }

    [Fact]
    public void Gaps_Stop_At_The_Limit()
    {
        var coverage = new SpanCoverage();
        coverage.Add(0, 1000);

        Assert.Equal(new[] { (1000, 3000) }, coverage.GapsWithin(3000).ToList());
        Assert.Empty(coverage.GapsWithin(800));
    }

    [Fact]
    public void Everything_Is_A_Gap_When_Nothing_Is_Covered()
    {
        var coverage = new SpanCoverage();

        Assert.Equal(new[] { (0, 5000) }, coverage.GapsWithin(5000).ToList());
    }
}
