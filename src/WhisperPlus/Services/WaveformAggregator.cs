using System;
using System.Threading;

namespace WhisperPlus.Services;

/// <summary>
/// Consumes raw PCM samples and produces a smoothed, normalized audio level (0..1) and
/// a peak value, both safe to bind to the UI. This is the bridge between NAudio's
/// high-frequency buffer callbacks (background thread) and the WPF waveform visual
/// (UI thread). Smoothing uses a one-pole low-pass so the meter looks natural rather
/// than jittery.
/// </summary>
public sealed class WaveformAggregator
{
    private const float Alpha = 0.35f;          // smoothing factor (higher = snappier)
    private const float Decay = 0.85f;          // per-sample decay applied when idle
    private const float NoiseFloor = 0.01f;     // ignore anything quieter than this

    private float _smoothed;
    private float _peak;
    private int _sampleRate;
    private int _channels;
    private volatile bool _enabled;

    /// <summary>Raised on the audio thread whenever a new level sample is computed.</summary>
    public event Action<float, float>? SampleAvailable; // (level, peak)

    /// <summary>Configures the aggregator for the incoming PCM stream shape.</summary>
    public void Configure(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = Math.Max(1, channels);
    }

    /// <summary>Enables/disables aggregation. When disabled, the level decays to zero.</summary>
    public void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>
    /// Processes a buffer of 16-bit PCM samples (interleaved channels). Computes the RMS
    /// across all samples, normalizes to 0..1, applies smoothing, and raises
    /// <see cref="SampleAvailable"/>. Called from NAudio's capture thread.
    /// </summary>
    public void Process(ReadOnlySpan<byte> buffer)
    {
        if (!_enabled || buffer.IsEmpty) return;

        // Each sample is 2 bytes (Int16). Walk 2 bytes at a time.
        var sampleCount = buffer.Length / 2;
        if (sampleCount == 0) return;

        double sumSq = 0;
        int n = 0;
        for (var i = 0; i + 1 < buffer.Length; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            double d = sample / 32768.0;
            sumSq += d * d;
            n++;
        }
        if (n == 0) return;

        var rms = (float)Math.Sqrt(sumSq / n);
        if (rms < NoiseFloor) rms = 0;

        _smoothed = (Alpha * rms) + ((1f - Alpha) * _smoothed);
        if (rms > _peak) _peak = rms;

        SampleAvailable?.Invoke(_smoothed, _peak);
    }

    /// <summary>Reads the current smoothed level and peak (UI thread, for binding refresh).</summary>
    public (float Level, float Peak) Read()
        => (Volatile.Read(ref _smoothed), Volatile.Read(ref _peak));

    /// <summary>Called by a UI timer to let the meter decay back toward zero when idle.</summary>
    public void ApplyIdleDecay()
    {
        _smoothed *= Decay;
        _peak *= Decay;
        if (_smoothed < 0.001f) _smoothed = 0;
        if (_peak < 0.001f) _peak = 0;
    }

    /// <summary>Resets the aggregator to a silent state.</summary>
    public void Reset()
    {
        _smoothed = 0;
        _peak = 0;
    }
}
