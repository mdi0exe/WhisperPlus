using System;
using WhisperPlus.Services;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for the speech-onset detector — the piece that decides which slice of a capture gets shown
/// to the language classifier.
///
/// <para>This matters more than its size suggests. Language routing used to classify the audio from
/// the instant the mic opened, which is the gap before the user starts talking. Shown silence,
/// Whisper's language head answers "English" with high confidence, so every session routed to
/// English no matter what was actually spoken. These tests pin the behaviour that fixes it: onset
/// lands on the speech, not on the lead-in.</para>
/// </summary>
public class SpeechOnsetDetectorTests
{
    private const int SampleRate = 16000;

    private static int Samples(int ms) => SampleRate * ms / 1000;

    /// <summary>
    /// Low-level room tone. Deterministic (seeded) so a failure is always reproducible, and shaped
    /// as noise rather than digital silence so the detector faces something realistic.
    /// </summary>
    private static float[] Silence(int ms, float amplitude = 0.0015f, int seed = 12345)
    {
        var random = new Random(seed);
        var buffer = new float[Samples(ms)];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = (float)(random.NextDouble() * 2 - 1) * amplitude;
        return buffer;
    }

    /// <summary>A loud tone standing in for speech; only its envelope matters to an energy VAD.</summary>
    private static float[] Speech(int ms, float amplitude = 0.25f)
    {
        var buffer = new float[Samples(ms)];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = (float)Math.Sin(2 * Math.PI * 220 * i / SampleRate) * amplitude;
        return buffer;
    }

    private static float[] Concat(params float[][] parts)
    {
        var total = 0;
        foreach (var part in parts) total += part.Length;

        var result = new float[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    [Fact]
    public void Silence_Alone_Never_Reports_Onset()
    {
        var detector = new SpeechOnsetDetector(SampleRate);

        detector.Feed(Silence(3000));

        // The exact bug: two seconds of room tone must not look like the start of an utterance.
        Assert.False(detector.HasOnset);
        Assert.Null(detector.OnsetSample);
    }

    [Fact]
    public void Finds_Onset_After_A_Pause()
    {
        var detector = new SpeechOnsetDetector(SampleRate);

        // The user presses the hotkey, hesitates for 800 ms, then speaks.
        detector.Feed(Concat(Silence(800), Speech(1000)));

        Assert.True(detector.HasOnset);
        var onset = detector.OnsetSample!.Value;

        // Onset lands at the speech, allowing for the deliberate pre-roll rewind before it and the
        // few frames of run-length confirmation after it.
        Assert.InRange(onset, Samples(800 - 200), Samples(800 + 150));
    }

    [Fact]
    public void Onset_Is_Reported_Once_And_Does_Not_Drift()
    {
        var detector = new SpeechOnsetDetector(SampleRate);
        detector.Feed(Concat(Silence(500), Speech(400)));

        Assert.True(detector.HasOnset);
        var first = detector.OnsetSample!.Value;

        // More speech, then a pause, then more speech again: the session's start does not move.
        detector.Feed(Concat(Speech(600), Silence(400), Speech(600)));

        Assert.Equal(first, detector.OnsetSample!.Value);
    }

    [Fact]
    public void Chunked_Feeding_Matches_A_Single_Feed()
    {
        var audio = Concat(Silence(700), Speech(900));

        var whole = new SpeechOnsetDetector(SampleRate);
        whole.Feed(audio);

        // The real capture arrives in ~50 ms buffers that do not align to the analysis frame, so
        // feeding in odd-sized chunks must give the same answer as one big call.
        var chunked = new SpeechOnsetDetector(SampleRate);
        const int chunk = 777;
        for (var offset = 0; offset < audio.Length; offset += chunk)
        {
            var length = Math.Min(chunk, audio.Length - offset);
            chunked.Feed(audio.AsSpan(offset, length));
        }

        Assert.True(whole.HasOnset);
        Assert.True(chunked.HasOnset);
        Assert.Equal(whole.OnsetSample!.Value, chunked.OnsetSample!.Value);
        Assert.Equal(whole.SamplesSeen, chunked.SamplesSeen);
    }

    [Fact]
    public void Ignores_A_Transient_Click()
    {
        var detector = new SpeechOnsetDetector(SampleRate);

        // A key release or mouse click: loud, but over in a few milliseconds. The run-length
        // requirement is what stops it from being mistaken for the start of speech.
        detector.Feed(Concat(Silence(400), Speech(15, amplitude: 0.6f), Silence(600)));

        Assert.False(detector.HasOnset);
    }

    [Fact]
    public void Speech_From_The_Very_Start_Still_Detected()
    {
        var detector = new SpeechOnsetDetector(SampleRate);

        // The user is already talking when the mic opens, so calibration measures speech rather
        // than the room. The clamped noise floor keeps onset reachable instead of never firing.
        detector.Feed(Speech(1500));

        Assert.True(detector.HasOnset);
        Assert.InRange(detector.OnsetSample!.Value, 0, Samples(400));
    }

    [Fact]
    public void BestGuess_Points_At_The_Loudest_Audio_When_No_Onset_Fires()
    {
        var detector = new SpeechOnsetDetector(SampleRate);

        // Nothing but room tone: there is no onset to find, and the fallback must still point
        // somewhere usable rather than at sample 0.
        detector.Feed(Silence(1500));

        Assert.False(detector.HasOnset);
        Assert.InRange(detector.BestGuessOnsetSample, 0, Samples(1500));
    }

    /// <summary>
    /// The threshold is derived from the noise floor upwards, which quietly assumes the floor sits well
    /// below the speech. On a microphone where it does not, that arithmetic ran clean past the signal:
    /// measured on this app's own recordings, a capture peaking at 0.036 was handed a speech threshold
    /// of 0.0435 and one peaking at 0.038 a threshold of 0.060. Nothing in either could clear its own
    /// threshold, so no onset ever fired, every window scored as a pause, and the classifier was never
    /// asked at all — the language came back "unclear" no matter what had been said.
    /// </summary>
    [Fact]
    public void Thresholds_Stay_Inside_The_Range_The_Microphone_Actually_Produces()
    {
        var detector = new SpeechOnsetDetector(SampleRate);

        // A low-gain microphone in a noisy room: speech only ~10 dB above the floor, and the floor
        // high enough that the usual SNR margin lands above the loudest frame in the capture.
        detector.Feed(Concat(Silence(400, amplitude: 0.02f), Speech(1500, amplitude: 0.055f)));

        Assert.True(detector.SpeechThreshold < detector.PeakLevel,
            $"threshold {detector.SpeechThreshold} is above the loudest frame {detector.PeakLevel}, " +
            "so nothing in this capture could ever count as speech");
        Assert.True(detector.PauseThreshold <= detector.SpeechThreshold);
        Assert.True(detector.HasOnset, "a quiet speaker should still register an onset");
    }

    [Fact]
    public void A_Quiet_Speaker_Now_Registers_An_Onset()
    {
        var detector = new SpeechOnsetDetector(SampleRate);

        // Speech barely above the room. This used to fall through to the loudest-frame fallback two and
        // a half seconds later, by which time a short sentence is already over.
        detector.Feed(Concat(Silence(600), Speech(600, amplitude: 0.0025f)));

        Assert.True(detector.HasOnset);
        Assert.InRange(detector.OnsetSample!.Value, 0, Samples(900));
    }

    [Fact]
    public void Room_Tone_Alone_Never_Registers_An_Onset()
    {
        // The range ceiling must not drag the threshold down into the noise it is measuring. Several
        // seconds of nothing but room tone, at a few different levels, must all stay silent.
        foreach (var amplitude in new[] { 0.0004f, 0.0015f, 0.01f, 0.05f })
        {
            var detector = new SpeechOnsetDetector(SampleRate);
            detector.Feed(Silence(3000, amplitude: amplitude, seed: 4242));

            Assert.False(detector.HasOnset, $"room tone at amplitude {amplitude} was taken for speech");
        }
    }

    [Fact]
    public void BestGuess_Is_Zero_Before_Any_Audio()
    {
        var detector = new SpeechOnsetDetector(SampleRate);

        Assert.Equal(0, detector.BestGuessOnsetSample);
        Assert.Equal(0, detector.SamplesSeen);
        Assert.Equal(0, detector.ElapsedMs);
    }

    [Fact]
    public void Tracks_Elapsed_Time()
    {
        var detector = new SpeechOnsetDetector(SampleRate);

        detector.Feed(Silence(250));
        Assert.Equal(250, detector.ElapsedMs);

        detector.Feed(Silence(250));
        Assert.Equal(500, detector.ElapsedMs);
        Assert.Equal(Samples(500), detector.SamplesSeen);
    }

    [Fact]
    public void Speech_Threshold_Separates_A_Pause_From_An_Utterance()
    {
        var detector = new SpeechOnsetDetector(SampleRate);
        detector.Feed(Concat(Silence(600), Speech(800)));

        // This is the yardstick the session uses to decide whether a candidate detection window is
        // worth handing to the classifier at all. A window of room tone must fall below it and a
        // window of speech above it, or re-scanning would simply move from one pause to the next.
        Assert.True(SpeechOnsetDetector.LevelOf(Silence(2500, seed: 999)) < detector.SpeechThreshold);
        Assert.True(SpeechOnsetDetector.LevelOf(Speech(2500)) >= detector.SpeechThreshold);
    }

    [Fact]
    public void Speech_Threshold_Rises_With_A_Noisy_Room()
    {
        var quiet = new SpeechOnsetDetector(SampleRate);
        quiet.Feed(Silence(400));

        var noisy = new SpeechOnsetDetector(SampleRate);
        noisy.Feed(Silence(400, amplitude: 0.012f));

        // The floor adapts to the room rather than assuming a fixed one — but never sinks below the
        // absolute floor, so a digitally silent input cannot drive it to zero.
        Assert.True(noisy.SpeechThreshold > quiet.SpeechThreshold);
        Assert.True(quiet.SpeechThreshold > 0);
    }

    /// <summary>
    /// The bug behind "it can't detect my language when I speak quietly": every detection window of
    /// a soft speaker sits below the absolute speech threshold, so the session skipped all of them
    /// as pauses and never ran a single probe. The pause yardstick has to sit between this speaker's
    /// room and this speaker's speech, wherever that happens to be.
    /// </summary>
    [Fact]
    public void Pause_Threshold_Passes_Quiet_Speech_But_Still_Rejects_The_Room()
    {
        var detector = new SpeechOnsetDetector(SampleRate);

        // Speech barely above the room.
        var quiet = Speech(1200, amplitude: 0.003f);
        detector.Feed(Concat(Silence(600), quiet));

        Assert.True(SpeechOnsetDetector.LevelOf(quiet) >= detector.PauseThreshold);
        Assert.True(SpeechOnsetDetector.LevelOf(Silence(2500, seed: 999)) < detector.PauseThreshold);
    }

    /// <summary>
    /// For anyone speaking at a normal level the adaptive threshold must not loosen anything: it
    /// collapses to the same speech threshold the detector already used.
    /// </summary>
    [Fact]
    public void Pause_Threshold_Matches_The_Speech_Threshold_For_A_Normal_Speaker()
    {
        var detector = new SpeechOnsetDetector(SampleRate);
        detector.Feed(Concat(Silence(600), Speech(1200)));

        Assert.Equal(detector.SpeechThreshold, detector.PauseThreshold);
        Assert.True(detector.PeakLevel > detector.SpeechThreshold);
    }

    [Fact]
    public void Pause_Threshold_Is_Positive_Before_Any_Audio()
    {
        // Probes can be gated before calibration finishes; a zero threshold would pass silence.
        Assert.True(new SpeechOnsetDetector(SampleRate).PauseThreshold > 0f);
    }

    [Fact]
    public void Level_Of_An_Empty_Span_Is_Zero()
    {
        Assert.Equal(0f, SpeechOnsetDetector.LevelOf(ReadOnlySpan<float>.Empty));
    }

    // ---- Voiced ratio ----
    //
    // What a whole-window average cannot tell you: how much of the window is actually speech. A gate
    // on mean level waves through a window that is four fifths silence with one loud syllable in it,
    // and the classifier then answers from its prior rather than from the audio.

    [Fact]
    public void Voiced_Ratio_Is_One_For_Continuous_Speech()
    {
        var ratio = SpeechOnsetDetector.VoicedRatio(Speech(1000), threshold: 0.05f, frameSamples: Samples(20));

        Assert.Equal(1.0, ratio, 3);
    }

    [Fact]
    public void Voiced_Ratio_Is_Zero_For_Silence()
    {
        var ratio = SpeechOnsetDetector.VoicedRatio(Silence(1000), threshold: 0.05f, frameSamples: Samples(20));

        Assert.Equal(0.0, ratio, 3);
    }

    [Fact]
    public void Voiced_Ratio_Sees_What_A_Mean_Level_Cannot()
    {
        // A fifth speech, four fifths room tone. Its average level clears the same threshold the old
        // gate used, so the window looked like speech; per frame, it plainly is not.
        var window = Concat(Speech(200, amplitude: 0.5f), Silence(800));

        Assert.True(SpeechOnsetDetector.LevelOf(window) > 0.05f);
        Assert.Equal(0.2, SpeechOnsetDetector.VoicedRatio(window, 0.05f, Samples(20)), 2);
    }

    [Fact]
    public void Voiced_Ratio_Judges_A_Sub_Frame_Span_As_A_Whole()
    {
        var tiny = Speech(5);

        Assert.Equal(1.0, SpeechOnsetDetector.VoicedRatio(tiny, 0.05f, Samples(20)), 3);
        Assert.Equal(0.0, SpeechOnsetDetector.VoicedRatio(Silence(5), 0.05f, Samples(20)), 3);
    }

    [Fact]
    public void Voiced_Ratio_Of_Nothing_Is_Zero()
    {
        Assert.Equal(0.0, SpeechOnsetDetector.VoicedRatio(ReadOnlySpan<float>.Empty, 0.05f, Samples(20)));
        Assert.Equal(0.0, SpeechOnsetDetector.VoicedRatio(Speech(100), 0.05f, frameSamples: 0));
    }

    [Fact]
    public void Voiced_Ratio_Uses_This_Speakers_Own_Level()
    {
        // A quiet speaker: every frame sits under the absolute speech threshold, but the detector
        // scales to their peak, so their speech still reads as speech rather than as a pause.
        var detector = new SpeechOnsetDetector(SampleRate);
        detector.Feed(Silence(200, amplitude: 0.0002f));
        var quiet = Speech(1000, amplitude: 0.01f);
        detector.Feed(quiet);

        Assert.True(detector.VoicedRatioOf(quiet) > 0.9);
        Assert.True(detector.VoicedRatioOf(Silence(1000, amplitude: 0.0002f)) < 0.1);
    }

    [Fact]
    public void Rejects_A_Nonsense_Sample_Rate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeechOnsetDetector(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeechOnsetDetector(-16000));
    }
}
