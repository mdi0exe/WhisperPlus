using System.Collections.Generic;
using NAudio.Wave;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Enumerates available audio input (microphone) devices via NAudio's WaveIn API.
/// Returns a list suitable for binding to a dropdown, with the system default first.
/// </summary>
public sealed class AudioDeviceService
{
    /// <summary>
    /// Lists all WaveIn-capable capture devices. The first entry is always a synthetic
    /// "Default" device (index -1) so users can defer to Windows' chosen mic.
    /// </summary>
    public IReadOnlyList<AudioDeviceItem> ListInputDevices()
    {
        var items = new List<AudioDeviceItem>
        {
            new(Index: -1, Name: "System Default Microphone", IsDefault: true),
        };

        var count = WaveIn.DeviceCount;
        for (var i = 0; i < count; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            // NAudio caps.ProductName is null-padded to 32 chars; trim trailing nulls.
            var name = caps.ProductName?.TrimEnd('\0') ?? $"Device {i}";
            items.Add(new AudioDeviceItem(i, name, IsDefault: false));
        }

        return items;
    }

    /// <summary>
    /// The <see cref="WaveInEvent.DeviceNumber"/> to open for a stored
    /// <see cref="AppSettings.RecordingDeviceIndex"/>.
    ///
    /// <para>The stored -1 means "whatever microphone Windows is set to use", and winmm expresses
    /// that as <see cref="WaveMapper"/> — which is also -1, so it is passed straight through.
    /// Coercing it to 0 instead, as this app used to, does <em>not</em> mean the default: device 0
    /// is simply the first entry winmm happens to enumerate, which on a typical laptop is the
    /// external 3.5&#160;mm jack rather than the built-in array. With nothing plugged into that jack
    /// the capture is a floating input — mains hum and little else — so dictation recorded a signal
    /// no speech model could read: language identification found none of the user's languages in
    /// it, and every session fell back to the default engine.</para>
    ///
    /// <para>An index that no longer exists (the device was unplugged, or the list reordered) falls
    /// back to the mapper for the same reason: opening some other device because it inherited the
    /// number is worse than letting Windows choose.</para>
    /// </summary>
    public static int ResolveDeviceNumber(int storedIndex)
    {
        if (storedIndex < 0) return WaveMapper;
        return storedIndex < WaveIn.DeviceCount ? storedIndex : WaveMapper;
    }

    /// <summary>
    /// winmm's <c>WAVE_MAPPER</c>: "let Windows pick the default capture device". NAudio passes
    /// <see cref="WaveInEvent.DeviceNumber"/> to <c>waveInOpen</c> unchanged, so -1 arrives as the
    /// mapper constant.
    /// </summary>
    public const int WaveMapper = -1;
}
