using NAudio.Wave;
using WhisperPlus.Services;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Which physical microphone the app opens for a stored device index.
///
/// <para>This is not the fussy detail it looks like. The app stores -1 for "System Default
/// Microphone" and used to open device 0 for it, which is a different thing entirely: device 0 is
/// whichever input winmm enumerates first, typically the external 3.5 mm jack. Recording a floating
/// jack yields mains hum, language identification finds none of the user's languages in it, and
/// every dictation falls back to the default engine — with nothing anywhere saying the microphone
/// was the problem.</para>
/// </summary>
public class AudioDeviceServiceTests
{
    [Fact]
    public void System_Default_Opens_The_Wave_Mapper_Not_Device_Zero()
    {
        Assert.Equal(AudioDeviceService.WaveMapper, AudioDeviceService.ResolveDeviceNumber(-1));
        Assert.NotEqual(0, AudioDeviceService.ResolveDeviceNumber(-1));
    }

    [Fact]
    public void An_Index_That_No_Longer_Exists_Falls_Back_To_The_Wave_Mapper()
    {
        // Unplugging a device renumbers the rest, so a stale index would otherwise silently open
        // whichever device inherited the number.
        Assert.Equal(
            AudioDeviceService.WaveMapper,
            AudioDeviceService.ResolveDeviceNumber(WaveIn.DeviceCount + 5));
    }

    [Fact]
    public void A_Device_The_User_Picked_By_Hand_Is_Opened_As_Chosen()
    {
        if (WaveIn.DeviceCount == 0) return; // no capture hardware on this machine

        Assert.Equal(0, AudioDeviceService.ResolveDeviceNumber(0));
    }

    [Fact]
    public void The_Device_List_Offers_System_Default_First()
    {
        var devices = new AudioDeviceService().ListInputDevices();

        Assert.NotEmpty(devices);
        Assert.Equal(-1, devices[0].Index);
        Assert.True(devices[0].IsDefault);
    }
}
