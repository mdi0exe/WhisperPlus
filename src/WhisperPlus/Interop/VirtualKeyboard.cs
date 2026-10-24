using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace WhisperPlus.Interop;

/// <summary>
/// Synthesizes keystrokes through SendInput, and keeps the modifier keys out of their way.
///
/// <para>Every synthesized chord in this app is sent while a global hotkey is being pressed, so the
/// modifiers of that hotkey are still down when the chord goes out and the target application sees
/// the union of the two: a Ctrl+V sent while Shift is held arrives as Ctrl+Shift+V, which most apps
/// treat as an entirely different command (paste-as-plain-text, or nothing at all) — that is why
/// dictated text sometimes never appeared in the focused box.</para>
///
/// <para>Waiting for the user to let go handles the common case, but not a modifier that is latched
/// rather than held: with Sticky Keys a single Ctrl press stays logically down with the physical key
/// released, and <see cref="Win32.GetAsyncKeyState"/> — which reports the physical key — says it is
/// up. <see cref="ReleaseModifiers"/> therefore consults the logical state too and synthesizes the
/// missing key-ups, which also clears a Sticky Keys latch.</para>
/// </summary>
public static class VirtualKeyboard
{
    // ---- Virtual-key codes ----
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;   // Alt
    public const ushort VK_LEFT = 0x25;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_TAB = 0x09;
    public const ushort VK_C = 0x43;
    public const ushort VK_V = 0x56;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_LSHIFT = 0xA0;
    public const ushort VK_RSHIFT = 0xA1;
    public const ushort VK_LCONTROL = 0xA2;
    public const ushort VK_RCONTROL = 0xA3;
    public const ushort VK_LMENU = 0xA4;
    public const ushort VK_RMENU = 0xA5;

    /// <summary>
    /// Every modifier that can alter how a synthesized chord is interpreted. The side-specific codes
    /// come first because they are what a key-up actually has to name; the generic ones are checked
    /// last so a latch reported only against <c>VK_CONTROL</c> is still cleared.
    /// </summary>
    private static readonly ushort[] Modifiers =
    {
        VK_LCONTROL, VK_RCONTROL, VK_LSHIFT, VK_RSHIFT,
        VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN,
        VK_CONTROL, VK_SHIFT, VK_MENU,
    };

    /// <summary>Settling time after clearing modifiers, before the chord that needed them clear.</summary>
    private const int ReleaseSettleMs = 25;

    /// <summary>
    /// Presses <paramref name="keys"/> in order and releases them in reverse, i.e. the usual
    /// modifier-first chord shape: <c>Chord(VK_CONTROL, VK_V)</c> is a paste.
    /// </summary>
    public static void Chord(params ushort[] keys)
    {
        if (keys is null || keys.Length == 0) return;

        // One SendInput for the whole chord. Sending each key separately and sleeping between them
        // blocked the calling thread for ~10 ms per key — and when that thread was the WPF UI
        // thread the message pump stalled with it. The queue keeps the order, which is all a chord
        // needs: modifiers down, key down, key up, modifiers up.
        var inputs = new List<INPUT>(keys.Length * 2);
        foreach (var key in keys) inputs.Add(KeyInput(key, keyUp: false));
        for (var i = keys.Length - 1; i >= 0; i--) inputs.Add(KeyInput(keys[i], keyUp: true));

        SendAll(inputs, "chord");
    }

    public static void KeyDown(ushort vk) => Send(vk, keyUp: false);

    public static void KeyUp(ushort vk) => Send(vk, keyUp: true);

    /// <summary>
    /// Types <paramref name="text"/> as Unicode keystrokes instead of pasting it.
    ///
    /// <para>This is the clipboard-free way to insert text: <c>KEYEVENTF_UNICODE</c> delivers the
    /// characters straight to the focused control, so nothing is written to the Windows clipboard and
    /// nothing shows up in clipboard history. Newlines and tabs are sent as real keys, because many
    /// controls ignore a Unicode CR/LF.</para>
    /// </summary>
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // A modifier still down turns the typed characters into shortcuts: Ctrl+Enter instead of a
        // newline (which sends the message in most chat apps), Ctrl+A selecting everything instead
        // of typing an "a". Paste already clears them through ClearModifiersAndChord; typing needs
        // the same treatment, and it also covers a Sticky Keys latch.
        ReleaseModifiers();

        // Characters are queued and flushed in blocks instead of one SendInput per character.
        // A 200-character correction used to cost 400 syscalls; blocks of at most
        // MaxUnicodeBatchInputs keep the call bounded while still letting a long string arrive
        // in a handful of calls. Surrogate pairs stay adjacent within the same SendInput call,
        // which is what the input stack needs to reassemble an astral character.
        var batch = new List<INPUT>(MaxUnicodeBatchInputs);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '\r':
                    continue;
                case '\n':
                case '\t':
                    FlushUnicode(batch);
                    Chord(ch == '\n' ? VK_RETURN : VK_TAB);
                    break;
                default:
                    if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        var low = text[++i];
                        if (batch.Count + 4 > MaxUnicodeBatchInputs) FlushUnicode(batch);
                        batch.Add(UnicodeInput(ch, keyUp: false));
                        batch.Add(UnicodeInput(ch, keyUp: true));
                        batch.Add(UnicodeInput(low, keyUp: false));
                        batch.Add(UnicodeInput(low, keyUp: true));
                    }
                    else
                    {
                        if (batch.Count + 2 > MaxUnicodeBatchInputs) FlushUnicode(batch);
                        batch.Add(UnicodeInput(ch, keyUp: false));
                        batch.Add(UnicodeInput(ch, keyUp: true));
                    }
                    break;
            }
        }
        FlushUnicode(batch);
    }

    /// <summary>Upper bound on one SendInput call while typing (2 inputs per character).</summary>
    private const int MaxUnicodeBatchInputs = 256;

    private static void FlushUnicode(List<INPUT> batch)
    {
        if (batch.Count == 0) return;
        SendAll(batch, "unicode text");
        batch.Clear();
    }

    private static INPUT UnicodeInput(char ch, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0u),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = (keyUp ? KEYEVENTF_KEYUP : 0u) | (IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0u),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        VK_RCONTROL or VK_RMENU or VK_LWIN or VK_RWIN => true,
        VK_LEFT or 0x26 /* VK_UP */ or 0x27 /* VK_RIGHT */ or 0x28 /* VK_DOWN */ => true,
        0x21 /* VK_PRIOR/PGUP */ or 0x22 /* VK_NEXT/PGDN */ or 0x23 /* VK_END */ or 0x24 /* VK_HOME */
            or 0x2D /* VK_INSERT */ or 0x2E /* VK_DELETE */ => true,
        0x6F /* VK_DIVIDE */ => true,
        _ => false,
    };

    private static void SendAll(List<INPUT> inputs, string what)
    {
        if (inputs.Count == 0) return;
        var array = inputs.ToArray();
        if (NativeMethods.SendInput((uint)array.Length, array, INPUT_SIZE) != array.Length)
            Log.Warning("SendInput ({What}, {Count} events) failed (err {Err}).",
                what, array.Length, Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Switches the foreground window's input language to Persian or to the first non-Persian layout,
    /// the way Alt+Shift does: <c>WM_INPUTLANGCHANGEREQUEST</c> is posted to that window so the change
    /// applies to the app the user is typing into, not to this process.
    /// </summary>
    /// <param name="persian">True to select a Persian layout, false for any other installed one.</param>
    /// <returns>False when the requested kind of layout is not installed or the post failed.</returns>
    public static bool ActivateInputLanguage(bool persian)
    {
        try
        {
            var count = NativeMethods.GetKeyboardLayoutList(0, null);
            if (count <= 0) return false;

            var layouts = new IntPtr[count];
            count = NativeMethods.GetKeyboardLayoutList(count, layouts);
            if (count <= 0) return false;

            var target = IntPtr.Zero;
            if (persian)
            {
                for (var i = 0; i < count; i++)
                {
                    // The low word of an HKL is the language id; both Persian layouts (standard and
                    // legacy) share 0x0429, which is all that matters here.
                    if ((layouts[i].ToInt64() & 0xFFFF) == PersianLanguageId)
                    {
                        target = layouts[i];
                        break;
                    }
                }
            }
            else
            {
                // When switching to English/Latin, prioritize an English keyboard layout first
                // (Primary Language ID == 0x09, such as en-US 0x0409, en-GB 0x0809) so users with
                // 3+ languages installed do not switch to a third unrelated layout.
                for (var i = 0; i < count; i++)
                {
                    if ((layouts[i].ToInt64() & 0x03FF) == 0x09)
                    {
                        target = layouts[i];
                        break;
                    }
                }

                // If no English layout is found, fall back to the first non-Persian layout.
                if (target == IntPtr.Zero)
                {
                    for (var i = 0; i < count; i++)
                    {
                        if ((layouts[i].ToInt64() & 0xFFFF) != PersianLanguageId)
                        {
                            target = layouts[i];
                            break;
                        }
                    }
                }
            }

            if (target == IntPtr.Zero)
            {
                Log.Debug("No {Kind} keyboard layout is installed; not switching.", persian ? "Persian" : "non-Persian");
                return false;
            }

            var foreground = Win32.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            var posted = NativeMethods.PostMessage(foreground, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, target);
            if (!posted) Log.Debug("WM_INPUTLANGCHANGEREQUEST was refused by the foreground window.");
            return posted;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not switch the input language.");
            return false;
        }
    }

    private const int PersianLanguageId = 0x0429;
    private const int WM_INPUTLANGCHANGEREQUEST = 0x0050;

    /// <summary>
    /// Releases every modifier that is down, then sends the chord, so the target app sees exactly
    /// the keys named here and nothing else.
    ///
    /// <para>Call this from inside <c>ForegroundWindow.WithForegroundFocus</c>. That is not a style
    /// preference: while input is attached to the foreground thread, this thread shares that thread's
    /// input state, which is what makes the logical check below meaningful — and that state is
    /// precisely the one deciding how the chord gets interpreted.</para>
    /// </summary>
    public static void ClearModifiersAndChord(params ushort[] keys)
    {
        ReleaseModifiers();
        Chord(keys);
    }

    /// <summary>
    /// An unassigned virtual key, used only as a mask (the <c>{vkE8}</c> trick AutoHotkey uses).
    /// </summary>
    private const ushort VkMask = 0xE8;

    /// <summary>
    /// Sends a keypress the shell ignores, so that the Windows key is not seen as pressed — and
    /// released — on its own.
    ///
    /// <para>The shell opens the Start menu when the Windows key goes down and comes back up without
    /// another key in between. Swallowing a shortcut's trigger key (Win+Shift+S) leaves exactly that
    /// shape, and so does releasing a stuck Win key. Injecting an unrelated key in between is what
    /// makes the shell treat the sequence as a combination rather than a tap.</para>
    /// </summary>
    public static void MaskWindowsKey() => Chord(VkMask);

    /// <summary>True when any modifier is physically held down right now.</summary>
    public static bool AnyModifierHeld()
    {
        foreach (var vk in Modifiers) if (IsPhysicallyDown(vk)) return true;
        return false;
    }

    /// <summary>
    /// Synthesizes a key-up for every modifier that is down — physically held, or latched by Sticky
    /// Keys with the physical key already released. Returns true if anything had to be released, in
    /// which case the settling delay has already been taken.
    /// </summary>
    public static bool ReleaseModifiers()
    {
        var released = 0;
        foreach (var vk in Modifiers)
        {
            if (!IsPhysicallyDown(vk) && !IsLogicallyDown(vk)) continue;

            // Releasing Win by itself is a Start-menu tap as far as the shell is concerned, and releasing
            // Alt by itself activates the active window's menu bar or ribbon. Mask both with a dummy key.
            if (vk is VK_LWIN or VK_RWIN or VK_MENU or VK_LMENU or VK_RMENU) MaskWindowsKey();

            KeyUp(vk);
            released++;
        }

        if (released == 0) return false;

        Log.Debug("Released {Count} modifier key(s) that were still down before synthesizing input.", released);
        Thread.Sleep(ReleaseSettleMs);
        return true;
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the user to let go of the modifiers they are
    /// physically holding — the hotkey's own, usually. Letting them finish their key-ups is the
    /// polite path: the app never has a key it thinks is held released underneath it.
    ///
    /// <para>Only physical keys are waited on. A latched or stuck modifier is not something the user
    /// is about to release, so waiting on it would just burn the whole timeout;
    /// <see cref="ClearModifiersAndChord"/> deals with those at the moment of sending.</para>
    /// </summary>
    public static async Task PrepareForChordAsync(int timeoutMs, CancellationToken ct = default)
    {
        for (var waited = 0; waited < timeoutMs; waited += 15)
        {
            if (!AnyModifierHeld())
            {
                // Let the app finish processing the real key-ups before injecting our own.
                await Task.Delay(30, ct).ConfigureAwait(false);
                return;
            }
            await Task.Delay(15, ct).ConfigureAwait(false);
        }

        Log.Debug("Modifier keys still held after {Ms} ms; they will be released before sending input.", timeoutMs);
    }

    /// <summary>Whether the physical key is down. System-wide, so it is valid from any thread.</summary>
    private static bool IsPhysicallyDown(ushort vk) => (Win32.GetAsyncKeyState(vk) & DownMask) != 0;

    /// <summary>
    /// Whether the key is down as the calling thread's input state sees it — which is how a Sticky
    /// Keys latch shows up, the physical key having long since come back up. Only meaningful on a
    /// thread that receives keyboard input, or one attached to such a thread.
    /// </summary>
    private static bool IsLogicallyDown(ushort vk) => (Win32.GetKeyState(vk) & DownMask) != 0;

    private const short DownMask = unchecked((short)0x8000);

    private static void Send(ushort vk, bool keyUp) => SendAll(new List<INPUT>(1) { KeyInput(vk, keyUp) }, $"vk=0x{vk:X2}");

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private static readonly int INPUT_SIZE = Marshal.SizeOf<INPUT>();

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetKeyboardLayoutList(int nBuff, IntPtr[]? lpList);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    // The native INPUT union is sized for MOUSEINPUT (32 bytes on x64). Keeping that full size is
    // required even though only KEYBDINPUT is ever populated; a smaller managed union makes
    // SendInput reject every call with ERROR_INVALID_PARAMETER.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
