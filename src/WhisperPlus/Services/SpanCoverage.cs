using System;
using System.Collections.Generic;

namespace WhisperPlus.Services;

/// <summary>
/// Which parts of a timeline have been dealt with, kept as a merged, sorted set of half-open spans.
///
/// <para>Language identification needs this twice over, for two different reasons, which is why it is
/// its own type rather than a field on either user. The scheduler needs to know which speech has
/// <em>not</em> been examined, so it can look somewhere new instead of re-reading the same second.
/// The accumulator needs to know how much of a window <em>has</em> been examined, so a re-read counts
/// for what it is worth rather than as a fresh opinion.</para>
///
/// <para>Milliseconds are the unit throughout, and spans are half-open — <c>[start, end)</c> — so
/// adjacent spans merge without overlapping. Sizes are tiny (a handful of spans per utterance), so the
/// linear scans are cheaper than anything cleverer.</para>
/// </summary>
public sealed class SpanCoverage
{
    private readonly List<(int Start, int End)> _spans = new();

    /// <summary>The merged spans, in ascending order.</summary>
    public IReadOnlyList<(int Start, int End)> Spans => _spans;

    /// <summary>Total length covered.</summary>
    public int TotalMs
    {
        get
        {
            var total = 0;
            foreach (var (start, end) in _spans) total += end - start;
            return total;
        }
    }

    /// <summary>True when nothing has been covered yet.</summary>
    public bool IsEmpty => _spans.Count == 0;

    /// <summary>The end of the last covered span, or 0 when nothing is covered.</summary>
    public int EndMs => _spans.Count == 0 ? 0 : _spans[^1].End;

    /// <summary>Adds <c>[start, end)</c>, merging it into any span it touches or overlaps.</summary>
    public void Add(int start, int end)
    {
        if (end <= start) return;

        var from = start;
        var to = end;
        var kept = new List<(int Start, int End)>(_spans.Count + 1);

        foreach (var span in _spans)
        {
            // Strictly disjoint spans survive; touching ones are absorbed into the new bounds.
            if (span.End < from || span.Start > to) kept.Add(span);
            else
            {
                from = Math.Min(from, span.Start);
                to = Math.Max(to, span.End);
            }
        }

        kept.Add((from, to));
        kept.Sort((a, b) => a.Start.CompareTo(b.Start));

        _spans.Clear();
        _spans.AddRange(kept);
    }

    /// <summary>How much of <c>[start, end)</c> is already covered.</summary>
    public int OverlapWith(int start, int end)
    {
        if (end <= start) return 0;

        var overlap = 0;
        foreach (var (spanStart, spanEnd) in _spans)
        {
            if (spanStart >= end) break;   // sorted: nothing later can overlap either
            var from = Math.Max(spanStart, start);
            var to = Math.Min(spanEnd, end);
            if (to > from) overlap += to - from;
        }
        return overlap;
    }

    /// <summary>Share of <c>[start, end)</c> that is already covered, in [0, 1]. 0 for an empty span.</summary>
    public double OverlapFraction(int start, int end)
    {
        var length = end - start;
        if (length <= 0) return 0.0;
        return Math.Clamp(OverlapWith(start, end) / (double)length, 0.0, 1.0);
    }

    /// <summary>
    /// The uncovered spans within <c>[0, limitMs)</c>, in ascending order — the parts still worth
    /// looking at.
    /// </summary>
    public IEnumerable<(int Start, int End)> GapsWithin(int limitMs)
    {
        var cursor = 0;
        foreach (var (start, end) in _spans)
        {
            if (start >= limitMs) break;
            if (start > cursor) yield return (cursor, Math.Min(start, limitMs));
            cursor = Math.Max(cursor, end);
            if (cursor >= limitMs) yield break;
        }

        if (cursor < limitMs) yield return (cursor, limitMs);
    }

    /// <summary>Forgets everything, for a session that restarts its analysis.</summary>
    public void Clear() => _spans.Clear();
}
