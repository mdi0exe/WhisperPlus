using System;

namespace WhisperPlus.Services;

/// <summary>One stretch of the utterance to hand a language identifier, measured from the speech onset.</summary>
/// <param name="StartMs">Offset from the onset.</param>
/// <param name="LengthMs">How much audio to take.</param>
/// <param name="NovelMs">How much of it has not been examined before — 0 for a pure re-read.</param>
public readonly record struct ProbeWindow(int StartMs, int LengthMs, int NovelMs)
{
    /// <summary>One past the last millisecond of the window.</summary>
    public int EndMs => StartMs + LengthMs;
}

/// <summary>
/// Decides <em>which</em> audio to ask about next, by tracking what has already been examined and
/// always reaching for the earliest speech that has not been.
///
/// <para>This replaces a fixed table of offsets — <c>(0,1000) (0,2000) (0,3500) (3500,2500) …</c> —
/// whose first three entries were the same audio read three times. Re-reading the opening second with
/// a little more on the end is not a second opinion; it is the same opinion, and treating it as
/// independent evidence is how a wrong first answer used to confirm itself. What a second look is
/// actually worth comes from somewhere <em>else</em> in the utterance, and that is all this class
/// does: keep a coverage map, hand back the earliest gap in it, and refuse to run out of ideas
/// before the audio runs out.</para>
///
/// <para>The first window is deliberately short so an answer — and with it the model preload — arrives
/// as early as it did before. Later ones are as long as the backend can use, because by then latency
/// has already been paid and what is wanted is the best evidence per inference.</para>
///
/// <para>Pure arithmetic over milliseconds: it never sees audio, so its behaviour is fully testable
/// without a model. Deciding whether a window is worth classifying is the caller's job — it has the
/// samples — and the caller reports back with <see cref="MarkExamined"/> either way, so a stretch
/// rejected as a pause is not offered again.</para>
///
/// <para>Not thread-safe: it belongs to one session's pump thread.</para>
/// </summary>
public sealed class LanguageProbeScheduler
{
    private readonly SpanCoverage _examined = new();
    private readonly int _firstWindowMs;
    private readonly int _windowMs;
    private readonly int _minWindowMs;
    private readonly int _minNovelMs;
    private readonly int _maxWindows;

    /// <param name="firstWindowMs">
    /// Length of the opening window. Short, because nothing can be routed until it answers.
    /// </param>
    /// <param name="windowMs">
    /// Length of every later window — the backend's <see cref="ILanguageIdentifier.MaxWindowMs"/>,
    /// since past that it learns nothing more from extra audio.
    /// </param>
    /// <param name="minWindowMs">
    /// Least real audio worth an inference; below this the backend is padding more than it is
    /// listening.
    /// </param>
    /// <param name="maxWindows">
    /// Ceiling on windows per session, so a long dictation in a noisy room cannot spend the whole
    /// recording on language identification.
    /// </param>
    /// <param name="minNovelMs">
    /// Least unexamined audio a gap must hold to be worth returning to. Below this a gap is a sliver
    /// between two windows, not a new part of the utterance.
    /// </param>
    public LanguageProbeScheduler(
        int firstWindowMs = 1200,
        int windowMs = 4000,
        int minWindowMs = 1000,
        int maxWindows = 10,
        int minNovelMs = 400)
    {
        _minWindowMs = Math.Max(1, minWindowMs);
        _firstWindowMs = Math.Max(_minWindowMs, firstWindowMs);
        _windowMs = Math.Max(_firstWindowMs, windowMs);
        _maxWindows = Math.Max(1, maxWindows);
        _minNovelMs = Math.Clamp(minNovelMs, 1, _minWindowMs);
    }

    /// <summary>How many windows have been handed out.</summary>
    public int WindowsIssued { get; private set; }

    /// <summary>True once the session's window budget is spent.</summary>
    public bool IsExhausted => WindowsIssued >= _maxWindows;

    /// <summary>Speech that has been examined, whether or not it produced an answer.</summary>
    public SpanCoverage Examined => _examined;

    /// <summary>
    /// Picks the next window to classify, or returns false when there is nothing worth asking about
    /// yet.
    /// </summary>
    /// <param name="availableMs">Milliseconds of speech available from the onset onwards.</param>
    /// <param name="allowOverlap">
    /// Whether a window may reach back over audio already examined in order to reach a usable length.
    /// False while the user is still talking — waiting a moment for real new speech is better than
    /// paying for a re-read. True once the recording has ended, where waiting is no longer an option
    /// and a re-read that carries a sliver of new audio is the best offer left.
    /// </param>
    /// <param name="window">The window to classify, when this returns true.</param>
    public bool TryNext(int availableMs, bool allowOverlap, out ProbeWindow window)
    {
        window = default;
        if (IsExhausted || availableMs < _minWindowMs) return false;

        var desired = Math.Min(WindowsIssued == 0 ? _firstWindowMs : _windowMs, availableMs);

        foreach (var (gapStart, gapEnd) in _examined.GapsWithin(availableMs))
        {
            var novel = gapEnd - gapStart;
            if (novel < _minNovelMs) continue;

            // Prefer to start where the new audio starts and run forward, which keeps windows aligned
            // with the utterance rather than with the analysis history.
            var start = gapStart;
            var length = Math.Min(desired, availableMs - start);

            // While the user is still talking, a window must be the full length asked for. Settling for
            // whatever happens to have arrived looks harmless and is not: the pump ticks every 120 ms,
            // so the moment one more second exists it would be spent as a whole window, and a schedule
            // that should have taken four-second looks takes one-second ones instead — burning the
            // session's window budget on the opening seconds and never examining the rest. Waiting
            // costs nothing, because more audio is on its way by definition.
            var required = allowOverlap ? _minWindowMs : desired;

            if (length < required)
            {
                // Not enough room ahead of the gap. Reaching backwards over examined audio would buy a
                // usable length at the cost of counting some speech twice, which is worth it only when
                // no more audio is coming.
                if (!allowOverlap) continue;

                start = Math.Max(0, Math.Min(gapEnd, availableMs) - desired);
                length = Math.Min(desired, availableMs - start);
                if (length < _minWindowMs) continue;
            }

            var end = start + length;
            var overlap = _examined.OverlapWith(start, end);

            WindowsIssued++;
            window = new ProbeWindow(start, length, Math.Max(0, length - overlap));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Records that <paramref name="window"/> has been dealt with — classified, declined, or skipped as
    /// a pause. All three mean the same thing to the schedule: do not offer that audio again while
    /// there is anything unexamined left.
    /// </summary>
    public void MarkExamined(ProbeWindow window) => _examined.Add(window.StartMs, window.EndMs);

    /// <summary>Records an examined stretch by its bounds, for a caller not holding a window.</summary>
    public void MarkExamined(int startMs, int lengthMs) => _examined.Add(startMs, startMs + lengthMs);
}
