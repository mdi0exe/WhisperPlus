using System;
using WhisperPlus.Interop;

namespace WhisperPlus.Helpers;

/// <summary>
/// Ensures synthesized keyboard input (via SendInput) is delivered to the foreground
/// application the user actually meant to target. When a global hotkey fires, the WPF
/// UI thread is not necessarily the thread that owns keyboard focus, so a raw SendInput
/// keystroke can land on the wrong window. Temporarily attaching our input state to the
/// foreground window's thread makes the synthesized keys reach that window reliably.
/// </summary>
public static class ForegroundWindow
{
    /// <summary>
    /// Runs <paramref name="action"/> while the calling thread shares input focus/state
    /// with the current foreground window's thread. If there is no foreground window or
    /// the attach fails, <paramref name="action"/> runs anyway (best-effort fallback).
    /// </summary>
    public static void WithForegroundFocus(Action action)
    {
        if (action is null) return;

        var foreground = Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            action();
            return;
        }

        // A hung foreground window is a trap: AttachThreadInput joins this thread's input queue to
        // a thread that is not pumping messages, and SetForegroundWindow then blocks until that app
        // recovers — freezing the caller (the WPF UI thread, when this runs from a hotkey). Send the
        // input anyway: SendInput targets the focused window and does not need the attach.
        if (Win32.IsHungAppWindow(foreground))
        {
            action();
            return;
        }

        // Find the thread that owns the foreground window and our own thread id.
        var fgThread = Win32.GetWindowThreadProcessId(foreground, out _);
        var ourThread = Win32.GetCurrentThreadId();

        // Only attach when the windows live on different threads; attaching to our own
        // thread is pointless and AttachThreadInput would no-op.
        bool attached = false;
        if (fgThread != 0 && fgThread != ourThread)
        {
            try
            {
                attached = Win32.AttachThreadInput(ourThread, fgThread, fAttach: true);
            }
            catch (Exception)
            {
                attached = false;
            }
        }

        try
        {
            // Re-assert focus on the foreground window: while threads are attached,
            // SetForegroundWindow is permitted under the foreground-lock restrictions.
            if (attached) Win32.SetForegroundWindow(foreground);
            action();
        }
        finally
        {
            if (attached)
            {
                try { Win32.AttachThreadInput(ourThread, fgThread, fAttach: false); }
                catch (Exception) { /* best-effort detach */ }
            }
        }
    }
}
