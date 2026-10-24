using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using WhisperPlus.Helpers;
using WhisperPlus.Interop;

namespace WhisperPlus.Services;

/// <summary>
/// Captures the text currently selected in the foreground application, or — when nothing
/// is selected — the last typed word. Capture works by sending Ctrl+C (or selecting the previous
/// word first via Ctrl+Shift+Left, then Ctrl+C) and reading the clipboard text back.
///
/// <para>This clipboard-driven approach works across nearly all Windows apps (where UI Automation
/// is unreliable or unsupported) and is the same technique popular layout-fixers rely on.</para>
///
/// <para><b>This class deliberately does not save or restore the user's clipboard.</b> It is one
/// step of a sequence that goes on to overwrite the clipboard again when it pastes the correction,
/// so a backup taken here would be restored before the paste and then immediately displaced by it —
/// which is exactly how the layout fix used to leave the copied selection behind in place of
/// whatever the user actually had. The caller (<see cref="LayoutFixService"/>) takes one snapshot
/// around the whole operation and puts it back at the end.</para>
/// </summary>
public sealed class TextSelectionService
{
    /// <summary>
    /// How long to wait for the hotkey's own modifiers to come up before synthesizing Ctrl+C.
    /// Shorter than the paste's wait: nothing has been decided yet, so a slow start is felt as lag.
    /// </summary>
    private const int ModifierWaitMs = 400;

    /// <summary>Result of a capture attempt.</summary>
    public sealed record CaptureResult(bool Success, string Text, CaptureSource Source);

    /// <summary>
    /// Result of a clipboard-free capture. <paramref name="Supported"/> is false when the app does not
    /// expose its selection through UI Automation, which is the caller's signal that the clipboard
    /// path is the only way left.
    /// </summary>
    public sealed record ClipboardFreeCapture(bool Supported, CaptureResult Result);

    /// <summary>Where the captured text came from.</summary>
    public enum CaptureSource
    {
        /// <summary>Nothing was captured.</summary>
        None,
        /// <summary>An explicit text selection in the foreground app.</summary>
        Selection,
        /// <summary>The last typed word, captured after auto-selecting it.</summary>
        LastWord,
    }

    /// <summary>
    /// How long to wait for the target app to put the copied text on the clipboard. A fixed sleep
    /// cannot be right for every app: an editor fills it in ~10 ms, a browser or a remote session
    /// can take several times that, and reading too early looks exactly like "nothing was selected".
    /// </summary>
    private const int ClipboardWaitMs = 350;

    /// <summary>How often the clipboard is checked while waiting.</summary>
    private const int ClipboardPollMs = 15;

    /// <summary>
    /// Grace period between clearing the clipboard and sending Ctrl+C, so the target app has
    /// refocused after the hotkey release. Short, because the UI thread is released while it runs.
    /// </summary>
    private const int ClipboardSettleMs = 30;

    /// <summary>
    /// A captured "last word" longer than this, or containing whitespace, is not a word — the
    /// select-previous-word chord landed on a whole line or paragraph in an app that does not
    /// support word-wise selection, and pasting over it would destroy text the user did not select.
    /// </summary>
    private const int MaxFallbackWordLength = 64;

    /// <summary>
    /// Tries to capture the active selection. If empty, optionally selects the last word
    /// and captures that instead. All clipboard work runs on the UI (STA) thread.
    /// </summary>
    /// <param name="fallbackToLastWord">When true and no selection exists, select+read the last word.</param>
    public async Task<CaptureResult> CaptureAsync(bool fallbackToLastWord, CancellationToken ct = default)
    {
        // WM_HOTKEY arrives while the user is still physically holding its trigger keys. Both
        // synthesized chords below would otherwise be read as part of that original shortcut —
        // Ctrl+Shift+C rather than Ctrl+C. Any modifier still down after the wait (held, stuck, or
        // latched by Sticky Keys) is released outright.
        await VirtualKeyboard.PrepareForChordAsync(ModifierWaitMs, ct).ConfigureAwait(true);

        // 1. Try the current selection (Ctrl+C).
        var selection = await ReadClipboardAfterCopy(ct);
        if (!string.IsNullOrEmpty(selection))
        {
            // Deliberately not trimmed: the capture is pasted back over the selection, so dropping
            // a leading or trailing space would glue the corrected word to its neighbour. Only a
            // capture that is *entirely* whitespace counts as "nothing selected".
            return new CaptureResult(true, selection, CaptureSource.Selection);
        }

        // 2. No selection — select the previous word and copy it.
        if (fallbackToLastWord)
        {
            await Application.Current.Dispatcher.InvokeAsync(SendSelectPreviousWord);
            await Task.Delay(40, ct);
            var word = await ReadClipboardAfterCopy(ct);
            // Plausibility is judged on the trimmed token (word-wise selection usually takes the
            // neighbouring space with it), but the spacing itself is kept for the paste back.
            if (!string.IsNullOrEmpty(word) && IsPlausibleWord(word.Trim()))
            {
                return new CaptureResult(true, word, CaptureSource.LastWord);
            }

            if (!string.IsNullOrEmpty(word))
            {
                Log.Information("LayoutFix: the last-word fallback selected {Length} characters with whitespace; ignoring it.",
                    word.Length);
            }
        }

        return new CaptureResult(false, string.Empty, CaptureSource.None);
    }

    /// <summary>A single token: no whitespace and not absurdly long.</summary>
    private static bool IsPlausibleWord(string text)
    {
        if (text.Length > MaxFallbackWordLength) return false;
        foreach (var ch in text)
            if (char.IsWhiteSpace(ch)) return false;
        return true;
    }

    /// <summary>
    /// Reads the selection through UI Automation, without copying anything.
    ///
    /// <para>Nothing is written to the clipboard, so the operation leaves no trace in Windows
    /// clipboard history and the user's clipboard contents survive untouched. When the app does not
    /// expose TextPattern — or does not answer in time — <c>Supported</c> comes back false and the
    /// caller can decide whether the clipboard is an acceptable fallback.</para>
    /// </summary>
    public async Task<ClipboardFreeCapture> CaptureWithoutClipboardAsync(
        bool fallbackToLastWord, CancellationToken ct = default)
    {
        // Same modifier settling as the clipboard path: the hotkey's own keys must be up before the
        // selection chords are sent.
        await VirtualKeyboard.PrepareForChordAsync(ModifierWaitMs, ct).ConfigureAwait(true);

        var selection = await UiTextReader.ReadSelectionAsync(ct);
        if (selection is null && !UiTextReader.LastReadTimedOut)
        {
            // Chromium and Electron expose their accessibility tree only once a client asks for it,
            // and the very first query can come back empty while the engine spins up. One retry a
            // moment later is usually enough to get TextPattern, and it costs little when the app
            // genuinely has none.
            await Task.Delay(120, ct);
            selection = await UiTextReader.ReadSelectionAsync(ct);
        }

        if (selection is null)
            return new ClipboardFreeCapture(false, new CaptureResult(false, string.Empty, CaptureSource.None));

        if (!string.IsNullOrEmpty(selection))
            return new ClipboardFreeCapture(true, new CaptureResult(true, selection, CaptureSource.Selection));

        if (fallbackToLastWord)
        {
            await Application.Current.Dispatcher.InvokeAsync(SendSelectPreviousWord);
            await Task.Delay(40, ct);
            var word = await UiTextReader.ReadSelectionAsync(ct);
            if (!string.IsNullOrEmpty(word) && IsPlausibleWord(word.Trim()))
                return new ClipboardFreeCapture(true, new CaptureResult(true, word, CaptureSource.LastWord));

            // If reading via UIA timed out, failed, or came back empty after selecting the previous
            // word, return Supported=false so LayoutFixService falls back to the clipboard path and
            // captures the highlighted word rather than abandoning the active selection.
            //
            // The empty string matters as much as null here: an app that answers TextPattern but
            // reports "" has just had a word highlighted by Ctrl+Shift+Left and is refusing to
            // report it. Treating that as "supported, nothing selected" returned Success=false and
            // abandoned the very selection this method had just made.
            if (string.IsNullOrEmpty(word))
                return new ClipboardFreeCapture(false, new CaptureResult(false, string.Empty, CaptureSource.None));
        }

        // Read it, and there was nothing selected: a real "no capture", not a missing feature.
        return new ClipboardFreeCapture(true, new CaptureResult(false, string.Empty, CaptureSource.None));
    }

    /// <summary>
    /// Sends Ctrl+C and reads back whatever landed on the clipboard. The clipboard is cleared first
    /// so a copy that silently failed reads as "no selection" rather than as whatever was already
    /// there — quietly converting the user's unrelated clipboard text is far worse than doing nothing.
    ///
    /// <para><b>Why every wait here is an await and not a sleep.</b> <see cref="Clipboard"/> is
    /// thread-affine, so the work has to run on the UI (STA) thread — but it must not <em>hold</em>
    /// that thread while it waits. The screenshot shortcut is a <c>WH_KEYBOARD_LL</c> hook pumped by
    /// the same thread; if it stops pumping for longer than <c>LowLevelHooksTimeout</c> (~300 ms by
    /// default) Windows silently unhooks it, so the screenshot key would stop working, and the
    /// settings window would freeze for as long as the poll. Awaiting yields the thread back to the
    /// dispatcher, which keeps both the hook and the window responsive. This runs twice per fix
    /// (selection, then last word), so the old blocking version could stall the UI for ~760 ms.</para>
    /// </summary>
    private static async Task<string?> ReadClipboardAfterCopy(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var operation = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            string? result = null;
            try
            {
                // Diagnose which window currently has focus so we can tell whether the
                // synthesized Ctrl+C is reaching the intended target app.
                var fg = Win32.GetForegroundWindow();
                var fgThread = Win32.GetWindowThreadProcessId(fg, out uint fgPid);
                var ourThread = Win32.GetCurrentThreadId();
                Log.Debug("Capture: foreground HWND={Fg} tid={FgTid} pid={FgPid}; our tid={OurTid}; clipboard-has-text-before={Before}",
                    fg, fgThread, fgPid, ourThread, Clipboard.ContainsText());

                Clipboard.Clear();
                // Small delay so the target app has refocused after the hotkey release.
                await Task.Delay(ClipboardSettleMs, ct);
                SendCtrlC();

                // Poll instead of sleeping a fixed amount: the copy completes when the target app
                // gets round to it, and guessing wrong reads as "no selection".
                for (var waited = 0; waited < ClipboardWaitMs; waited += ClipboardPollMs)
                {
                    await Task.Delay(ClipboardPollMs, ct);
                    if (Clipboard.ContainsText())
                    {
                        result = Clipboard.GetText();
                        break;
                    }
                }

                Log.Debug("Capture: after Ctrl+C clipboard-has-text={After}", result is not null);
            }
            catch (OperationCanceledException)
            {
                // Cancelled mid-poll: report "nothing captured" rather than throwing across the
                // dispatcher boundary, which would surface as an unrelated unhandled exception.
                return null;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Ctrl+C capture failed.");
            }

            return result;
        });

        // The operation hands back the lambda's Task, not its result: unwrap both layers.
        return await operation.Task.Unwrap().ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the selection. Input focus is attached to the foreground app's thread so the
    /// synthesized Ctrl+C reaches it instead of (possibly) our own hidden window.
    /// </summary>
    private static void SendCtrlC() => ForegroundWindow.WithForegroundFocus(
        () => VirtualKeyboard.ClearModifiersAndChord(VirtualKeyboard.VK_CONTROL, VirtualKeyboard.VK_C));

    /// <summary>
    /// Selects the previous word (Ctrl+Shift+Left), to capture the last typed word when the user
    /// has nothing selected.
    /// </summary>
    private static void SendSelectPreviousWord() => ForegroundWindow.WithForegroundFocus(
        () => VirtualKeyboard.ClearModifiersAndChord(
            VirtualKeyboard.VK_CONTROL, VirtualKeyboard.VK_SHIFT, VirtualKeyboard.VK_LEFT));
}
