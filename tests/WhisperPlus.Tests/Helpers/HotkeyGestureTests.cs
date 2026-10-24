using WhisperPlus.Helpers;
using WhisperPlus.Interop;
using Xunit;

namespace WhisperPlus.Tests.Helpers;

/// <summary>
/// Unit tests for <see cref="HotkeyGesture"/> parsing/formatting. The parser feeds the
/// hotkey service, so its correctness is critical.
/// </summary>
public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Space", Win32.MOD_CONTROL | Win32.MOD_NOREPEAT, 0x20)]
    [InlineData("Ctrl+D", Win32.MOD_CONTROL | Win32.MOD_NOREPEAT, (uint)'D')]
    [InlineData("Ctrl+Alt+R", Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, (uint)'R')]
    [InlineData("Ctrl+Shift+P", Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, (uint)'P')]
    [InlineData("Control+Alt+S", Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, (uint)'S')]
    [InlineData("ctrl+alt+s", Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, (uint)'S')]
    [InlineData("Ctrl++", Win32.MOD_CONTROL | Win32.MOD_NOREPEAT, 0xBBu)]
    [InlineData("Ctrl+Plus", Win32.MOD_CONTROL | Win32.MOD_NOREPEAT, 0xBBu)]
    [InlineData("Ctrl+Shift++", Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, 0xBBu)]
    [InlineData("+", Win32.MOD_NOREPEAT, 0xBBu)]
    public void Parse_ValidGestures(string gesture, uint expectedMods, uint expectedVk)
    {
        var parsed = HotkeyGesture.Parse(gesture);
        Assert.NotNull(parsed);
        Assert.Equal(expectedMods, parsed!.Value.Modifiers & ~0u);
        Assert.Equal(expectedMods, parsed.Value.Modifiers);
        Assert.Equal(expectedVk, parsed.Value.VirtualKey);
    }

    [Theory]
    [InlineData("")]            // empty
    [InlineData("   ")]         // whitespace
    [InlineData("Ctrl")]        // modifier only, no key
    [InlineData("Ctrl+xyzzy")]  // unknown token
    public void Parse_Invalid_ReturnsNull(string? gesture)
    {
        Assert.Null(HotkeyGesture.Parse(gesture));
    }

    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(HotkeyGesture.Parse(null));
    }

    [Theory]
    [InlineData("Ctrl+Space", "Ctrl+Space")]
    [InlineData("Ctrl+Alt+Shift+Win+R", "Ctrl+Alt+Shift+Win+R")]
    [InlineData("Control+D", "Ctrl+D")]      // canonicalizes "Control" → "Ctrl"
    public void Format_Normalizes(string input, string expected)
    {
        var parsed = HotkeyGesture.Parse(input);
        Assert.NotNull(parsed);
        Assert.Equal(expected, HotkeyGesture.Format(parsed!.Value.Modifiers, parsed.Value.VirtualKey));
    }

    // ---- Punctuation must map to VK_OEM_*, not to whatever its ASCII code happens to be ----
    // Casting '-' straight to a virtual key produced 0x2D (VK_INSERT); '[' produced 0x5B, which is
    // the left Windows key. A hotkey the user rebound to Ctrl+[ therefore registered as Win+... .
    [Theory]
    [InlineData("Ctrl+-", 0xBD)]   // VK_OEM_MINUS
    [InlineData("Ctrl+=", 0xBB)]   // VK_OEM_PLUS
    [InlineData("Ctrl+[", 0xDB)]   // VK_OEM_4
    [InlineData("Ctrl+]", 0xDD)]   // VK_OEM_6
    [InlineData("Ctrl+\\", 0xDC)]  // VK_OEM_5
    [InlineData("Ctrl+;", 0xBA)]   // VK_OEM_1
    [InlineData("Ctrl+'", 0xDE)]   // VK_OEM_7
    [InlineData("Ctrl+,", 0xBC)]   // VK_OEM_COMMA
    [InlineData("Ctrl+.", 0xBE)]   // VK_OEM_PERIOD
    [InlineData("Ctrl+/", 0xBF)]   // VK_OEM_2
    [InlineData("Ctrl+`", 0xC0)]   // VK_OEM_3
    public void Parse_Punctuation_UsesOemVirtualKeys(string gesture, int expectedVk)
    {
        var parsed = HotkeyGesture.Parse(gesture);
        Assert.NotNull(parsed);
        Assert.Equal((uint)expectedVk, parsed!.Value.VirtualKey);
    }

    [Theory]
    [InlineData("Ctrl+-")]
    [InlineData("Ctrl+[")]
    [InlineData("Ctrl+.")]
    [InlineData("Ctrl+`")]
    public void Punctuation_RoundTripsThroughFormat(string gesture)
    {
        var parsed = HotkeyGesture.Parse(gesture);
        Assert.NotNull(parsed);
        var formatted = HotkeyGesture.Format(parsed!.Value.Modifiers, parsed.Value.VirtualKey);
        Assert.Equal(gesture, formatted);

        // And the formatted string parses back to the same virtual key.
        var again = HotkeyGesture.Parse(formatted);
        Assert.NotNull(again);
        Assert.Equal(parsed.Value.VirtualKey, again!.Value.VirtualKey);
    }

    [Fact]
    public void Format_FunctionKey()
    {
        var parsed = HotkeyGesture.Parse("Ctrl+F5");
        Assert.NotNull(parsed);
        Assert.Equal(0x74u, parsed!.Value.VirtualKey); // F5 = 0x74
        Assert.Equal("Ctrl+F5", HotkeyGesture.Format(parsed.Value.Modifiers, parsed.Value.VirtualKey));
    }
}
