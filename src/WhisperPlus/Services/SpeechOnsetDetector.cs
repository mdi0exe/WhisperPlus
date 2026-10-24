using System;
using System.Collections.Generic;

namespace WhisperPlus.Services;

/// <summary>
/// Finds the sample at which the user actually started talking, so language identification looks
/// at speech instead of at whatever was in the room while the mic was warming up.
///
/// <para>This exists because the mic opens on the hotkey but the user starts a beat later. Handing
/// that opening gap to a language classifier is not merely uninformative — it is actively
/// misleading: Whisper's language head answers "English" for near-silence with high confidence,
/// because English dominates its training distribution. Every session therefore routed to English
/// no matter what was spoken afterwards.</para>
///
/// <para>The detector is a plain short-term-energy VAD: RMS per 20 ms frame, an adaptive noise
/// floor measured from the opening of the capture, and a sustained run of frames above that floor
/// to declare onset. That is enough for dictation, where the speaker is close to the mic and the
/// decision only has to be roughly right — it picks the start of the window, and a window that
/// begins 100 ms early costs nothing. It deliberately avoids a second neural model on the hot path
/// when arithmetic settles it.</para>
///
/// <para>Not thread-safe: feed it from one thread (the session's pump).</para>
/// </summary>
public sealed class SpeechOnsetDetector
{
    /// <summary>Analysis frame length. Short enough to place onset precisely, long enough for a stable RMS.</summary>
    public const int FrameMs = 20;

    /// <summary>
    /// Opening span used to measure the room. The user has not begun speaking this early — that is
    /// the whole premise — so these frames characterize the noise floor.
    /// </summary>
    private const int CalibrationFrames = 10; // 200 ms

    /// <summary>
    /// How far above the noise floor a frame must sit to count as speech. ~9.5 dB: comfortably
    /// above room tone and mic hiss, below any actual utterance.
    /// </summary>
    private const float SnrRatio = 3.0f;

    /// <summary>
    /// Absolute floor (~-54 dBFS) so a silent room with a very low measured noise floor does not
    /// trigger on its own dither.
    ///
    /// <para>Deliberately below what a comfortable speaker produces: the SNR margin above is what
    /// separates speech from the room, and this only stops that margin from collapsing when the room
    /// is nearly silent. Set any higher and a soft speaker, or one sitting back from the mic, never
    /// registers an onset at all — their whole utterance goes to the loudest-frame fallback two and a
    /// half seconds later, by which time a short sentence is already over.</para>
    /// </summary>
    private const float AbsoluteFloor = 0.002f;

    /// <summary>
    /// Ceiling on the measured noise floor. If calibration happened to catch speech — the user
    /// started talking immediately — an unbounded floor would sit above everything and onset would
    /// never fire. Clamping keeps the detector usable in that case.
    /// </summary>
    private const float MaxNoiseFloor = 0.02f;

    /// <summary>
    /// Consecutive above-threshold frames required to declare onset (80 ms). A key release, a
    /// mouse click or a desk knock is a transient and dies well inside this; speech does not.
    /// </summary>
    private const int OnsetFrames = 4;

    /// <summary>
    /// Rewind applied to the declared onset. The run-length requirement means onset is recognized
    /// only after speech is already underway, and the leading consonant carries real information
    /// for language ID, so the window starts slightly before the trigger.
    /// </summary>
    private const int PreRollMs = 150;

    /// <summary>
    /// Percentile of the calibration frames taken as the noise floor. A median would still be
    /// pulled up by a click landing in the middle of a short calibration window; the lower
    /// percentile tracks the quiet background those frames are supposed to represent.
    /// </summary>
    private const double CalibrationPercentile = 0.3;

    /// <summary>
    /// Share of this speaker's loudest frame that still counts as speech rather than a pause.
    /// Ordinary dictation swings well over 12 dB between a stressed vowel and an unstressed one, so
    /// a quarter of the peak is comfortably inside the utterance.
    /// </summary>
    private const float QuietSpeechFraction = 0.25f;

    /// <summary>
    /// How far above the measured noise floor <see cref="PauseThreshold"/> must stay. Lower than
    /// <see cref="SnrRatio"/> — declaring an onset should be conservative, but deciding a window is
    /// worth *looking* at only has to clear the room.
    /// </summary>
    private const float PauseSnrRatio = 1.5f;

    /// <summary>
    /// Hard bottom for <see cref="PauseThreshold"/> (~-62 dBFS). Adapting to a quiet speaker must
    /// not adapt all the way down to a silent room, where anything at all would look like speech.
    /// </summary>
    private const float SilenceFloor = 0.0008f;

    private readonly int _sampleRate;
    private readonly int _frameSamples;
    private readonly int _preRollSamples;

    private readonly float[] _partial;
    private int _partialCount;

    private readonly List<float> _calibration = new(CalibrationFrames);
    private float _noiseFloor;
    private bool _calibrated;

    private long _framesSeen;
    private int _runLength;

    private float _loudestRms;
    private long _loudestFrame = -1;

    public SpeechOnsetDetector(int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        _sampleRate = sampleRate;
        _frameSamples = Math.Max(1, sampleRate * FrameMs / 1000);
        _preRollSamples = sampleRate * PreRollMs / 1000;
        _partial = new float[_frameSamples];
    }

    /// <summary>
    /// Index of the first speech sample, or null while the detector is still hearing background.
    /// Already includes the pre-roll rewind and is never negative.
    /// </summary>
    public long? OnsetSample { get; private set; }

    /// <summary>True once speech has been located.</summary>
    public bool HasOnset => OnsetSample is not null;

    /// <summary>Total samples fed so far — what the caller compares against to decide it has waited long enough.</summary>
    public long SamplesSeen { get; private set; }

    /// <summary>
    /// Where to start when onset was never confidently found: the loudest frame observed, minus the
    /// same pre-roll. A quiet or far-from-mic speaker still produces a peak somewhere in their
    /// speech, so this beats falling back to sample 0 — which is the silence that caused the bug.
    /// Returns 0 before any audio has been seen.
    /// </summary>
    public long BestGuessOnsetSample =>
        OnsetSample ?? (_loudestFrame < 0 ? 0 : Math.Max(0, _loudestFrame * _frameSamples - _preRollSamples));

    /// <summary>
    /// The level a stretch of audio has to reach before it counts as speech in <em>this</em> room:
    /// the measured noise floor plus the SNR margin, never below the absolute floor. Exposed so a
    /// caller choosing which part of the capture to hand a language classifier can tell a pause from
    /// speech using the same yardstick the onset decision used, instead of a second guess at what
    /// "quiet" means here. Usable before calibration completes, where it is simply the floor.
    /// </summary>
    public float SpeechThreshold => Ceiling(Math.Max(_noiseFloor * SnrRatio, AbsoluteFloor), SpeechHeadroom);

    /// <summary>
    /// Holds a threshold below what this microphone has actually been heard to produce.
    ///
    /// <para>Both thresholds are built from the noise floor upwards, which silently assumes the floor
    /// is well below the speech. On a microphone where it is not — a low input gain, a noisy room, a
    /// laptop's built-in mic — the arithmetic runs past the speech entirely: measured on this app's own
    /// recordings, a capture peaking at 0.036 was given a speech threshold of 0.0435 and one peaking at
    /// 0.038 a threshold of 0.060. Nothing in either recording could ever clear its own threshold, so
    /// no onset was ever declared, every window scored as a pause, and the classifier was never asked
    /// at all. The ceiling here is what keeps the thresholds inside the range the signal occupies.</para>
    ///
    /// <para>It only ever lowers a threshold. On a healthy close-mic capture the peak is far above the
    /// noise floor, the ceiling lands well above the SNR-derived value, and nothing changes.</para>
    /// </summary>
    private float Ceiling(float threshold, float headroom)
    {
        // Before any audio there is no range to reason about; the caller gets the absolute floor.
        if (_loudestRms <= 0f) return threshold;

        var floor = Math.Min(_noiseFloor, _loudestRms);
        var ceiling = floor + headroom * Math.Max(0f, _loudestRms - floor);

        // The ceiling must never be dragged down into the room itself. When nothing louder than the
        // noise has been heard — a capture with no speech in it yet — the range is meaningless and
        // scaling into it would put the threshold below frames of pure room tone, which vary by a few
        // percent either side of the floor. The detector would then declare an onset on silence,
        // trading the failure this ceiling fixes for a worse one.
        ceiling = Math.Max(ceiling, floor * MinCeilingSnr);

        return Math.Max(SilenceFloor, Math.Min(threshold, ceiling));
    }

    /// <summary>
    /// Where <see cref="SpeechThreshold"/> may sit within the range between the noise floor and the
    /// loudest frame heard. Above the midpoint, because declaring an onset should stay conservative:
    /// this is a backstop for a microphone whose speech never clears the usual margin, not a
    /// replacement for that margin.
    /// </summary>
    private const float SpeechHeadroom = 0.6f;

    /// <summary>
    /// The same for <see cref="PauseThreshold"/>, lower because deciding a window is worth *looking*
    /// at should err towards looking. A window wrongly skipped is never classified at all; a window
    /// wrongly examined costs one inference, and the classifier declines non-speech on its own.
    /// </summary>
    private const float PauseHeadroom = 0.25f;

    /// <summary>
    /// Least the range ceiling may sit above the measured noise floor, as a multiple of it. A 20 ms
    /// frame's level is stable to within a few percent, so 1.5× is many standard deviations clear of
    /// room tone — comfortably enough to keep silence out, while still leaving the ceiling far below
    /// the <see cref="SnrRatio"/> margin it exists to cap.
    /// </summary>
    private const float MinCeilingSnr = 1.5f;

    /// <summary>The loudest frame heard so far. Zero before any audio has been fed.</summary>
    public float PeakLevel => _loudestRms;

    /// <summary>
    /// Level below which a stretch of audio is treated as a pause and not worth classifying.
    ///
    /// <para>This is <see cref="SpeechThreshold"/> scaled to how loud this speaker actually is,
    /// never above it and never below the room. For a normal speaker the peak is far above the
    /// threshold and this simply *is* the threshold. For a quiet one — talking softly, sitting back
    /// from the mic, or running a low input gain — every window of their speech can sit below the
    /// absolute speech threshold, and gating on it skipped the entire utterance as a "pause": no
    /// probe ever ran, the finishing sweep found nothing either, and the session reported the
    /// language as unclear no matter what was said.</para>
    ///
    /// <para>Scaling to their peak lets that speech through; keeping it above the measured noise
    /// floor is what still holds the pauses out, since for a very quiet speaker the room and the
    /// speech are only a few dB apart and a fixed bottom would let both through.</para>
    /// </summary>
    public float PauseThreshold => Math.Min(
        SpeechThreshold,
        Ceiling(
            Math.Max(Math.Max(_loudestRms * QuietSpeechFraction, _noiseFloor * PauseSnrRatio), SilenceFloor),
            PauseHeadroom));

    /// <summary>
    /// Level of an arbitrary span, on the same scale as <see cref="SpeechThreshold"/>. Unlike the
    /// per-frame analysis this accepts any length, so a caller can score a whole candidate window.
    /// An empty span scores 0.
    /// </summary>
    public static float LevelOf(ReadOnlySpan<float> samples) => samples.IsEmpty ? 0f : Rms(samples);

    /// <summary>
    /// What fraction of <paramref name="samples"/> is actually speech: the share of its
    /// <see cref="FrameMs"/> frames whose level clears <paramref name="threshold"/>.
    ///
    /// <para>This is what a whole-window RMS cannot tell you. A window that is four fifths silence
    /// with one loud syllable in it has the same average level as one that is speech throughout, and a
    /// mean-level gate waves both through — so the classifier gets handed mostly silence and answers
    /// from its prior, which is the failure this whole area of the code keeps circling back to. Knowing
    /// the ratio lets a caller both skip such a window and, when it uses it anyway, discount what it
    /// says.</para>
    /// </summary>
    public static double VoicedRatio(ReadOnlySpan<float> samples, float threshold, int frameSamples)
    {
        if (samples.IsEmpty || frameSamples <= 0) return 0.0;

        // Anything shorter than a frame can only be judged as a whole.
        if (samples.Length < frameSamples) return Rms(samples) >= threshold ? 1.0 : 0.0;

        var frames = 0;
        var voiced = 0;
        for (var offset = 0; offset + frameSamples <= samples.Length; offset += frameSamples)
        {
            frames++;
            if (Rms(samples.Slice(offset, frameSamples)) >= threshold) voiced++;
        }

        return frames == 0 ? 0.0 : voiced / (double)frames;
    }

    /// <summary>
    /// <see cref="VoicedRatio(ReadOnlySpan{float}, float, int)"/> measured against this room and this
    /// speaker — the same <see cref="PauseThreshold"/> the rest of the session judges pauses by, so a
    /// quiet speaker is not written off as silence.
    /// </summary>
    public double VoicedRatioOf(ReadOnlySpan<float> samples) =>
        VoicedRatio(samples, PauseThreshold, _frameSamples);

    /// <summary>
    /// Consumes newly captured samples. Audio is analyzed in whole frames; a trailing remainder is
    /// held until the next call, so callers may feed arbitrary chunk sizes. Once onset is found the
    /// remaining work is trivial, but feeding continues harmlessly.
    /// </summary>
    public void Feed(ReadOnlySpan<float> samples)
    {
        SamplesSeen += samples.Length;

        while (!samples.IsEmpty)
        {
            var take = Math.Min(_frameSamples - _partialCount, samples.Length);
            samples[..take].CopyTo(_partial.AsSpan(_partialCount));
            _partialCount += take;
            samples = samples[take..];

            if (_partialCount < _frameSamples) return;

            ProcessFrame(_partial);
            _partialCount = 0;
        }
    }

    private void ProcessFrame(ReadOnlySpan<float> frame)
    {
        var frameIndex = _framesSeen++;
        var rms = Rms(frame);

        if (rms > _loudestRms)
        {
            _loudestRms = rms;
            _loudestFrame = frameIndex;
        }

        if (!_calibrated)
        {
            _calibration.Add(rms);
            if (_calibration.Count >= CalibrationFrames) Calibrate();
            // Calibration frames are the room, by assumption — never speech onset.
            return;
        }

        if (OnsetSample is not null) return;

        // The property, not a second copy of the formula: the two drifting apart is how the range
        // ceiling above would have applied to every caller except the one that declares the onset.
        if (rms >= SpeechThreshold)
        {
            if (++_runLength >= OnsetFrames)
            {
                // Rewind to the first frame of the run, then apply the pre-roll.
                var runStart = (frameIndex - OnsetFrames + 1) * _frameSamples;
                OnsetSample = Math.Max(0, runStart - _preRollSamples);
            }
        }
        else
        {
            _runLength = 0;
        }
    }

    private void Calibrate()
    {
        var sorted = _calibration.ToArray();
        Array.Sort(sorted);

        var index = (int)(sorted.Length * CalibrationPercentile);
        if (index >= sorted.Length) index = sorted.Length - 1;

        _noiseFloor = Math.Min(sorted[index], MaxNoiseFloor);
        _calibrated = true;
    }

    private static float Rms(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (var sample in frame) sum += (double)sample * sample;
        return (float)Math.Sqrt(sum / frame.Length);
    }

    /// <summary>Milliseconds of audio consumed so far, for callers reasoning in time rather than samples.</summary>
    public int ElapsedMs => (int)(SamplesSeen * 1000 / _sampleRate);
}
