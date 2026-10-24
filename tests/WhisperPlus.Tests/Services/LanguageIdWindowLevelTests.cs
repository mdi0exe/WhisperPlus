using System;
using WhisperPlus.Services;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for the level of the window handed to a language identifier — the part that decides whether
/// a quiet speaker gets an answer at all. Its length is covered alongside the catalog, in
/// <see cref="LanguageIdWindowTests"/>.
///
/// <para>Neither backend is level-invariant. Silero reads raw samples and Whisper builds a mel
/// spectrogram from them, so audio recorded an order of magnitude below normal speech is not merely
/// "quiet" to them, it is a different input: Silero spreads its probability mass across languages
/// nobody speaks and declines, Whisper falls back on its English prior. Lifting the window to a
/// normal level is what makes a softly-spoken sentence answerable.</para>
/// </summary>
public class LanguageIdWindowLevelTests
{
    private const int SampleRate = 16000;

    private static int Samples(int ms) => SampleRate * ms / 1000;

    /// <summary>A tone at a given amplitude, standing in for speech at some recording level.</summary>
    private static float[] Tone(int ms, float amplitude)
    {
        var buffer = new float[Samples(ms)];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = (float)Math.Sin(2 * Math.PI * 220 * i / SampleRate) * amplitude;
        return buffer;
    }

    private static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0f;
        double sum = 0;
        foreach (var sample in samples) sum += (double)sample * sample;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    // ---- Level ----

    [Fact]
    public void Quiet_Audio_Is_Lifted_Towards_Normal_Speech_Level()
    {
        var quiet = Tone(2000, amplitude: 0.006f);   // ~-47 dBFS: a soft speaker, or a distant mic

        var window = LanguageIdWindow.Build(quiet, SampleRate, 1000, 4000);

        // Landed in the neighbourhood of ordinary dictation rather than staying near the noise floor.
        Assert.InRange(Rms(window), 0.03f, 0.08f);
        Assert.True(Rms(window) > Rms(quiet) * 5);
    }

    [Fact]
    public void Normal_Audio_Is_Left_Exactly_As_It_Was()
    {
        var normal = Tone(2000, amplitude: 0.25f);

        var window = LanguageIdWindow.Build(normal, SampleRate, 1000, 4000);

        // Already loud enough: rescaling working audio would be a change with no upside.
        Assert.Equal(1f, LanguageIdWindow.GainFor(normal));
        for (var i = 0; i < window.Length; i++) Assert.Equal(normal[i], window[i]);
    }

    [Fact]
    public void Gain_Never_Pushes_The_Waveform_Into_Clipping()
    {
        // A quiet recording with one loud transient in it — a desk knock during soft speech. The
        // level wants a large gain; the peak is what stops it from being applied.
        var audio = Tone(2000, amplitude: 0.004f);
        audio[1000] = 0.8f;
        audio[1001] = -0.8f;

        var window = LanguageIdWindow.Build(audio, SampleRate, 1000, 4000);

        Assert.True(Peak(window) <= 1f);
    }

    [Fact]
    public void Digital_Silence_Is_Not_Amplified()
    {
        var silence = new float[Samples(2000)];

        var window = LanguageIdWindow.Build(silence, SampleRate, 1000, 4000);

        Assert.Equal(1f, LanguageIdWindow.GainFor(silence));
        foreach (var sample in window) Assert.Equal(0f, sample);
    }

    [Fact]
    public void Gain_Is_Capped_So_Near_Silence_Is_Not_Turned_Into_Speech()
    {
        // Far below anything anyone spoke into a mic. Amplifying it without limit would hand the
        // classifier a full-scale rendition of the room's own noise.
        var floor = Tone(2000, amplitude: 0.00001f);

        Assert.True(LanguageIdWindow.GainFor(floor) <= 32f);
        Assert.True(Rms(LanguageIdWindow.Build(floor, SampleRate, 1000, 4000)) < 0.01f);
    }

    [Fact]
    public void Gain_Of_An_Empty_Window_Is_Neutral()
    {
        Assert.Equal(1f, LanguageIdWindow.GainFor(ReadOnlySpan<float>.Empty));
    }

    /// <summary>
    /// The level is measured over the real audio, not the zero padding: including the padding would
    /// understate a short clip's level by the padding ratio and over-amplify it by the same factor.
    /// </summary>
    [Fact]
    public void Padding_Does_Not_Skew_The_Measured_Level()
    {
        // 250 ms of audio already above the target level, padded out to a 1 s window. Measured over
        // the speech it needs no gain; measured over the padded window — three quarters of which is
        // zeros — it would look 6 dB quieter than it is and get amplified for no reason.
        var quarterSecond = Tone(250, amplitude: 0.12f);

        var window = LanguageIdWindow.Build(quarterSecond, SampleRate, 1000, 4000);

        Assert.Equal(Samples(1000), window.Length);
        Assert.Equal(1f, LanguageIdWindow.GainFor(quarterSecond));
        for (var i = 0; i < quarterSecond.Length; i++) Assert.Equal(quarterSecond[i], window[i]);
    }
}
