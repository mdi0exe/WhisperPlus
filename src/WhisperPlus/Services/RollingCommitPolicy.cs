using System;
using System.Collections.Generic;
using System.Text;

namespace WhisperPlus.Services;

/// <summary>
/// How a rolling Whisper decode was split: the segments old enough to keep, the still-unsettled
/// text to show as a preview, and how far into the decoded window the committed part reached.
/// </summary>
/// <param name="CommittedText">Text that has settled and will not be decoded again.</param>
/// <param name="PreviewText">The unsettled tail, shown live but subject to change.</param>
/// <param name="CommittedSamples">
/// Samples consumed by the committed text, relative to the start of the decoded window. The
/// caller advances its window origin by this much; 0 means nothing settled this round.
/// </param>
public readonly record struct RollingCommit(string CommittedText, string PreviewText, int CommittedSamples);

/// <summary>
/// Decides which part of a rolling Whisper decode has settled.
///
/// <para>Whisper is not a streaming engine: to show text while the user is still speaking, the
/// tail of the buffer has to be re-decoded periodically. Re-decoding <em>everything</em> every
/// time is wasteful and gets worse the longer someone talks, so segments that ended well before
/// the end of the audio are "committed": their text is kept verbatim and their audio is dropped
/// from the window, leaving only a short tail to redo next round.</para>
///
/// <para>The lag matters. Whisper revises the last words of a window once it hears what comes
/// next, so a segment is only trusted once at least <c>lagMs</c> of audio exists after it.</para>
///
/// <para>Pure and side-effect free, so the windowing arithmetic is unit-testable without a model.</para>
/// </summary>
public static class RollingCommitPolicy
{
    /// <summary>
    /// How much audio must follow a segment before its text is trusted. Whisper reconsiders the
    /// end of a window as more context arrives, so committing too eagerly bakes in words that
    /// the next pass would have corrected.
    /// </summary>
    public const int DefaultLagMs = 1500;

    /// <summary>
    /// Splits <paramref name="segments"/> — the result of decoding a window that holds
    /// <paramref name="windowSamples"/> samples at <paramref name="sampleRate"/> — into a
    /// committed prefix and a live preview.
    /// </summary>
    /// <param name="segments">Decoded segments, in order, with timestamps relative to the window.</param>
    /// <param name="windowSamples">Length of the decoded window in samples.</param>
    /// <param name="sampleRate">Sample rate of the window (16 kHz throughout this app).</param>
    /// <param name="lagMs">Trailing audio required before a segment is committed.</param>
    /// <param name="final">
    /// True when the user has stopped talking and no more audio is coming, in which case there is
    /// nothing left to revise and every segment is committed.
    /// </param>
    public static RollingCommit Split(
        IReadOnlyList<TranscribedSegment> segments,
        int windowSamples,
        int sampleRate = 16000,
        int lagMs = DefaultLagMs,
        bool final = false)
    {
        if (segments is null || segments.Count == 0 || sampleRate <= 0)
        {
            return new RollingCommit("", "", 0);
        }

        var windowMs = windowSamples <= 0 ? 0 : (long)windowSamples * 1000 / sampleRate;

        // Everything ending at or before this point has enough trailing context to be trusted.
        // When finalizing there is no more context to wait for, so the cutoff is the whole window.
        var cutoffMs = final ? long.MaxValue : windowMs - Math.Max(0, lagMs);

        var committed = new StringBuilder();
        var preview = new StringBuilder();
        var committedEndMs = 0L;
        var settling = false;

        foreach (var segment in segments)
        {
            var text = segment.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;

            var endMs = (long)segment.End.TotalMilliseconds;

            // Segments arrive in order, so once one is too recent to trust, every later one is
            // too — even if a stray timestamp would suggest otherwise.
            if (settling || endMs > cutoffMs)
            {
                settling = true;
                Append(preview, text);
                continue;
            }

            Append(committed, text);
            if (endMs > committedEndMs) committedEndMs = endMs;
        }

        // Never claim more audio than the window actually held; a rounded-up timestamp would
        // otherwise skip past samples that were never transcribed.
        var committedSamples = committed.Length == 0
            ? 0
            : (int)Math.Min(windowSamples, committedEndMs * sampleRate / 1000);

        return new RollingCommit(committed.ToString(), preview.ToString(), committedSamples);
    }

    /// <summary>Joins segment texts with single spaces, without a leading one.</summary>
    private static void Append(StringBuilder builder, string text)
    {
        if (builder.Length > 0) builder.Append(' ');
        builder.Append(text);
    }
}
