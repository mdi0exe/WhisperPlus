using System;
using System.Runtime.InteropServices;

namespace WhisperPlus.Interop;

/// <summary>
/// Win32 constants, structs, and P/Invoke signatures used across the app.
/// Kept in one place so each native API surface is declared exactly once. Made public so
/// the test project can assert against modifier flags.
/// </summary>
public static class Win32
{
    // ---- Window messages ----
    public const int WM_HOTKEY = 0x0312;

    // ---- Modifier keys for RegisterHotKey ----
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // ---- DwmSetWindowAttribute attribute ids ----
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // Corner preferences
    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;

    // System backdrop types (Win11 22H2+)
    public const int DWMSBT_AUTO = 0;
    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_MAINWINDOW = 2; // Mica
    public const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    public const int DWMSBT_TABBEDWINDOW = 4; // Tabbed Mica

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// True when any of <paramref name="keys"/> is physically down.
    ///
    /// <para>Callers pass the generic key together with its left/right variants: injected input and
    /// some keyboard drivers update only <c>VK_LCONTROL</c>/<c>VK_RCONTROL</c> and leave
    /// <c>GetAsyncKeyState(VK_CONTROL)</c> at zero even while the key is held.</para>
    /// </summary>
    public static bool IsAnyDown(params int[] keys)
    {
        foreach (var key in keys)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }

    /// <summary>
    /// The key's state as the calling thread's message queue sees it. Unlike
    /// <see cref="GetAsyncKeyState"/> this reports a Sticky Keys latch, where the modifier is
    /// logically down although the physical key has been released.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int vKey);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// True when the window has stopped pumping messages. Attaching our input queue to a thread
    /// that is not responding makes this thread wait on it, so callers check this first.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsHungAppWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SendNotifyMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const uint HWND_BROADCAST = 0xFFFF;
}
