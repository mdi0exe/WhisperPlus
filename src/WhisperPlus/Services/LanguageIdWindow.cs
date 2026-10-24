using System;

namespace WhisperPlus.Services;

/// <summary>
/// Shapes the audio handed to a language identifier. Both backends want the same thing — a bounded
/// window, never shorter than some minimum, at a level they can actually work with — so the
/// arithmetic lives in one place.
/// </summary>
public static class LanguageIdWindow
{
    /// <summary>
    /// Level the window is brought up to when it is quieter than this: roughly -24 dBFS RMS, which
    /// is where ordinary close-mic dictation sits.
    /// </summary>
    private const float TargetRms = 0.06f;

    /// <summary>
    /// Most a window may be amplified (~30 dB). Past this the "speech" being lifted is mostly the
    /// noise floor, and pretending otherwise would turn room tone into a confident language.
    /// </summary>
    private const float MaxGain = 32f;

    /// <summary>Peak the gain is held below, so lifting the level never clips the waveform.</summary>
    private const float PeakCeiling = 0.99f;

    /// <summary>Below this there is no signal to scale — scaling it would only amplify dither.</summary>
    private const float SilentRms = 1e-6f;

    /// <summary>
    /// Copies the leading <paramref name="maxWindowMs"/> of <paramref name="samples"/>, zero-padded
    /// up to <paramref name="minWindowMs"/> so the model always has enough to build features from,
    /// and lifts a quiet window to a level the model was trained on.
    ///
    /// <para>The caller is responsible for having sliced <paramref name="samples"/> so that it
    /// *starts at speech* — see <see cref="SpeechOnsetDetector"/>. Padding here adds silence at the
    /// end, which is harmless; silence at the beginning is what broke detection in the first place.</para>
    ///
    /// <para>The gain matters because neither backend is level-invariant. Silero sees raw samples
    /// and Whisper builds a mel spectrogram from them, so a speaker who talks quietly, sits back from
    /// the mic, or has a low input gain hands both models something an order of magnitude smaller
    /// than what they were trained on: Silero spreads its mass over unrelated languages and declines,
    /// Whisper falls back on its English prior. Amplifying costs one pass over a few thousand
    /// samples and makes a quiet utterance look like an ordinary one.</para>
    /// </summary>
    public static float[] Build(float[] samples, int sampleRate, int minWindowMs, int maxWindowMs)
    {
        var maxSamples = (int)((long)sampleRate * maxWindowMs / 1000);
        var minSamples = (int)((long)sampleRate * minWindowMs / 1000);

        var take = Math.Min(samples.Length, maxSamples);
        var window = new float[Math.Max(take, minSamples)];
        Array.Copy(samples, window, take);

        // Measured over the real audio only: including the zero padding would understate the level
        // of a short clip and over-amplify it by exactly the padding ratio.
        var gain = GainFor(window.AsSpan(0, take));
        if (gain > 1f)
        {
            for (var i = 0; i < take; i++) window[i] *= gain;
        }
        return window;
    }

    /// <summary>
    /// The factor that brings <paramref name="speech"/> up to <see cref="TargetRms"/>, capped by
    /// <see cref="MaxGain"/> and by whatever keeps the peak under <see cref="PeakCeiling"/>. Never
    /// less than 1: audio that is already loud enough is left exactly as it was, since the models
    /// handle it fine today and quietly rescaling it would be a change with no upside.
    /// </summary>
    public static float GainFor(ReadOnlySpan<float> speech)
    {
        if (speech.IsEmpty) return 1f;

        double sum = 0;
        var peak = 0f;
        foreach (var sample in speech)
        {
            sum += (double)sample * sample;
            var magnitude = Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
        }

        var rms = (float)Math.Sqrt(sum / speech.Length);
        if (rms < SilentRms || peak <= 0f) return 1f;

        var gain = Math.Min(TargetRms / rms, MaxGain);
        gain = Math.Min(gain, PeakCeiling / peak);
        return gain > 1f ? gain : 1f;
    }
}
