using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Serilog;

namespace WhisperPlus.Services;

/// <summary>
/// Reads the text currently selected in the foreground application through UI Automation, i.e.
/// <b>without touching the clipboard</b>.
///
/// <para>Why this exists: the classic trick — clear the clipboard, send Ctrl+C, read it back — leaves
/// a trace in Windows clipboard history (Win+V), in clipboard managers and in cloud clipboard sync,
/// and it destroys the user's clipboard for the duration. TextPattern reads the selection directly
/// from the accessibility tree, so nothing is copied at all.</para>
///
/// <para>Not every app exposes TextPattern (games, some Electron and custom-drawn controls), and a
/// busy app can make a UIA call slow, so this is a best-effort reader: it answers within
/// <paramref name="timeoutMs"/> or gives up, and the caller falls back to the clipboard.</para>
/// </summary>
public static class UiTextReader
{
    /// <summary>Upper bound for a UIA round-trip before the caller should use another method.</summary>
    public const int DefaultTimeoutMs = 900;

    /// <summary>True if the most recent selection read timed out rather than returned null quickly.</summary>
    public static bool LastReadTimedOut { get; private set; }

    /// <summary>
    /// The selected text in the focused element, or null when the app does not expose it (or takes
    /// too long to answer). An empty selection returns an empty string.
    /// </summary>
    public static async Task<string?> ReadSelectionAsync(CancellationToken ct, int timeoutMs = DefaultTimeoutMs)
    {
        var fg = Interop.Win32.GetForegroundWindow();
        if (fg != IntPtr.Zero && Interop.Win32.IsHungAppWindow(fg))
        {
            LastReadTimedOut = false;
            Log.Debug("Foreground window is hung; skipping UI Automation selection read.");
            return null;
        }

        var work = Task.Run(ReadSelectionCore, ct);
        var finished = await Task.WhenAny(work, Task.Delay(timeoutMs, ct)).ConfigureAwait(true);
        if (finished != work)
        {
            LastReadTimedOut = true;
            Log.Debug("UI Automation selection read timed out after {Ms} ms.", timeoutMs);
            return null;
        }

        LastReadTimedOut = false;
        try { return await work.ConfigureAwait(true); }
        catch (Exception ex)
        {
            Log.Debug(ex, "UI Automation selection read failed.");
            return null;
        }
    }

    private static string? ReadSelectionCore()
    {
        AutomationElement? focused;
        try
        {
            focused = AutomationElement.FocusedElement;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "UI Automation could not resolve the focused element.");
            return null;
        }

        if (focused is null) return null;
        if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
        if (pattern is not TextPattern text) return null;

        var ranges = text.GetSelection();
        if (ranges is null || ranges.Length == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var range in ranges)
            sb.Append(range.GetText(-1));
        return sb.ToString();
    }
}
