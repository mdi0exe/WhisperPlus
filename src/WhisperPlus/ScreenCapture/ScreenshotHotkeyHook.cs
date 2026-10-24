using System;
using System.Runtime.InteropServices;
using Serilog;
using WhisperPlus.Helpers;
using WhisperPlus.Interop;

namespace WhisperPlus.ScreenCapture;

/// <summary>
/// A global keyboard hook dedicated to the screenshot shortcut.
///
/// <para><b>Why a hook instead of RegisterHotKey.</b> Windows claims <c>Win+Shift+S</c> for the
/// Snipping Tool at a level RegisterHotKey cannot win: the call simply fails, or succeeds and
/// never fires. A <c>WH_KEYBOARD_LL</c> hook is called before the shell's own handling, so
/// consuming the keystroke there both triggers our capture and prevents Snipping Tool from
/// opening — which is what "make this the default instead of Windows" means in practice.</para>
///
/// <para><b>Cost.</b> The hook lives on the UI thread's message pump and does nothing but compare
/// a virtual-key code against the configured gesture, so it is idle-cheap: no polling, no timer,
/// no allocation per keystroke.</para>
/// </summary>
public sealed class ScreenshotHotkeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLKHF_UP = 0x80;

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // The delegate must stay referenced for as long as the hook is installed, otherwise the GC
    // collects the thunk and Windows calls into freed memory.
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook = IntPtr.Zero;

    private uint _modifiers;
    private uint _virtualKey;
    private bool _hasGesture;
    private bool _triggerHeld;

    /// <summary>Raised on the UI thread when the configured gesture is pressed.</summary>
    public event Action? Pressed;

    public ScreenshotHotkeyHook() => _proc = HookProc;

    /// <summary>Whether the hook reacts to the gesture at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true the keystroke is consumed, so the Windows Snipping Tool does not also open.
    /// When false the shortcut fires but Windows handles the key as usual.
    /// </summary>
    public bool SwallowKeystroke { get; set; } = true;

    /// <summary>Sets the gesture, e.g. <c>"Win+Shift+S"</c>. An unparsable string disables matching.</summary>
    public void SetGesture(string? gesture)
    {
        _hasGesture = false;
        var parsed = HotkeyGesture.Parse(gesture);
        if (parsed is null)
        {
            Log.Warning("Screenshot hotkey '{Gesture}' is not a valid gesture", gesture);
            return;
        }
        _modifiers = parsed.Value.Modifiers & ~Win32.MOD_NOREPEAT;
        _virtualKey = parsed.Value.VirtualKey;
        _hasGesture = _virtualKey != 0;
    }

    /// <summary>Installs the hook. Safe to call repeatedly.</summary>
    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Log.Warning("Could not install the keyboard hook (error {Error}); screenshot hotkey inactive",
                Marshal.GetLastWin32Error());
        else
            Log.Information("Screenshot keyboard hook installed");
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _triggerHeld = false;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        // Injected input is ignored so a macro or another tool cannot drive the screenshot key.
        if ((info.flags & LLKHF_INJECTED) != 0)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
        int vk = (int)info.vkCode;

        // Never swallow a modifier's release. Doing that (an earlier version consumed the Win
        // key-up to keep the Start menu from opening) leaves the shell convinced that Win is
        // still held down, so every later keystroke arrives as a Win+key shortcut: "e" opens
        // Explorer, "r" the Run box, and so on. Only the trigger key itself is consumed; the
        // modifiers are always passed through so their state stays truthful.

        if (!Enabled || !_hasGesture || (!isDown && !isUp))
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        if (isUp)
        {
            if (vk == (int)_virtualKey)
            {
                _triggerHeld = false;
                if (SwallowKeystroke && MatchesModifiers())
                    return new IntPtr(1);
            }
            else if (IsModifier(vk))
            {
                // Releasing any modifier resets trigger-held state so an unobserved key-up
                // (e.g. focus switched to an elevated window) never leaves the hotkey permanently locked.
                _triggerHeld = false;
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (!isDown) return CallNextHookEx(_hook, nCode, wParam, lParam);
        if (vk != (int)_virtualKey) return CallNextHookEx(_hook, nCode, wParam, lParam);
        if (!MatchesModifiers())
        {
            _triggerHeld = false;
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        if (_triggerHeld) return CallNextHookEx(_hook, nCode, wParam, lParam); // auto-repeat

        _triggerHeld = true;

        try { Pressed?.Invoke(); }
        catch (Exception ex) { Log.Error(ex, "Screenshot hotkey handler failed"); }

        if (!SwallowKeystroke) return CallNextHookEx(_hook, nCode, wParam, lParam);

        // Swallowing the trigger of a Win-based gesture leaves the shell with Win down and up and
        // nothing in between — a bare Windows-key tap, i.e. the Start menu opening behind the
        // overlay. A mask keypress in between is what AutoHotkey and PowerToys do for the same
        // reason. The modifiers themselves are still passed through, so no key is left "stuck".
        if ((_modifiers & Win32.MOD_WIN) != 0)
        {
            try { VirtualKeyboard.MaskWindowsKey(); }
            catch (Exception ex) { Log.Debug(ex, "Could not mask the Windows key press."); }
        }

        return new IntPtr(1);
    }

    /// <summary>
    /// Compares the physical modifier state with the gesture. Extra modifiers mean "not our
    /// shortcut", so Ctrl+Win+Shift+S is left alone.
    ///
    /// <para>Both the generic and the left/right virtual keys are probed: injected input and some
    /// keyboard drivers only update <c>VK_LCONTROL</c>/<c>VK_RCONTROL</c>, leaving
    /// <c>GetAsyncKeyState(VK_CONTROL)</c> at zero even though the user is holding Ctrl.</para>
    /// </summary>
    private bool MatchesModifiers()
    {
        uint state = 0;
        if (IsAnyDown(VK_CONTROL, VK_LCONTROL, VK_RCONTROL)) state |= Win32.MOD_CONTROL;
        if (IsAnyDown(VK_MENU, VK_LMENU, VK_RMENU)) state |= Win32.MOD_ALT;
        if (IsAnyDown(VK_SHIFT, VK_LSHIFT, VK_RSHIFT)) state |= Win32.MOD_SHIFT;
        if (IsAnyDown(VK_LWIN, VK_RWIN)) state |= Win32.MOD_WIN;
        return state == _modifiers;
    }

    private static bool IsModifier(int vk) =>
        vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT
           or VK_CONTROL or VK_LCONTROL or VK_RCONTROL
           or VK_MENU or VK_LMENU or VK_RMENU
           or VK_LWIN or VK_RWIN;

    private static bool IsAnyDown(params int[] keys)
    {
        foreach (var key in keys)
            if (IsDown(key)) return true;
        return false;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void Dispose()
    {
        Uninstall();
        Pressed = null;
    }
}
