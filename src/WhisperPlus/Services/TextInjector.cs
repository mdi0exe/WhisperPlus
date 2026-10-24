using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using WhisperPlus.Helpers;
using WhisperPlus.Interop;

namespace WhisperPlus.Services;

/// <summary>
/// Injects text into the currently focused input field by putting it on the clipboard and
/// sending a Ctrl+V paste via SendInput. This works across virtually all Windows apps
/// (browsers, editors, Office, chat) because it relies only on standard paste semantics.
/// </summary>
/// <remarks>
/// <para><see cref="PasteAsync"/> pastes whatever is already on the clipboard and leaves it
/// alone; <see cref="InjectAsync"/> writes the text itself and restores the prior clipboard
/// afterwards. Dictation uses the former (the transcript is meant to stay on the clipboard),
/// the layout fix uses the latter — or, when it needs the clipboard held across a capture as
/// well as a paste, owns the snapshot itself and calls <see cref="PasteAsync"/>. Restoring is
/// inherently racy — an app that reads the clipboard lazily can find the restored contents
/// instead of ours — so it is only done where leaving the text behind would be surprising.</para>
///
/// <para>All clipboard work runs on the STA UI thread, which is where OLE clipboard access is
/// allowed.</para>
/// </remarks>
public sealed class TextInjector
{
    /// <summary>
    /// How long to wait for the user to let go of the modifier keys before synthesizing a
    /// paste. Generous enough for a deliberate key release, short enough not to feel stuck.
    /// </summary>
    private const int ModifierWaitMs = 800;

    private readonly IClipboardAccess _clipboard;

    public TextInjector(IClipboardAccess clipboard)
    {
        _clipboard = clipboard;
    }

    /// <summary>
    /// Pastes <paramref name="text"/> into the foreground app and restores the prior
    /// clipboard. Use this when the text is a transient replacement (the layout fix) rather
    /// than something the user would expect to paste again.
    /// </summary>
    public async Task<bool> InjectAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // A detached snapshot, not the live IDataObject: that one belongs to whichever app put it
        // there and stops producing data the moment the clipboard is overwritten, so restoring it
        // would put back nothing at all.
        var backup = await _clipboard.CaptureAsync().ConfigureAwait(true);

        if (!await PasteAsync(text, ct).ConfigureAwait(true)) return false;

        await RestoreAsync(backup).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Puts a snapshot back on the clipboard, emptying it when the snapshot was taken of an empty
    /// clipboard — the user is entitled to get back exactly what they had, including nothing.
    /// </summary>
    public async Task RestoreAsync(ClipboardSnapshot backup)
    {
        var restored = backup.HasContent
            ? await _clipboard.RestoreAsync(backup).ConfigureAwait(true)
            : await _clipboard.ClearAsync().ConfigureAwait(true);

        if (!restored) Log.Warning("Could not put the previous clipboard contents back.");
    }

    /// <summary>Takes a detached snapshot of the clipboard, for a caller that will restore it.</summary>
    public Task<ClipboardSnapshot> CaptureClipboardAsync() => _clipboard.CaptureAsync();

    /// <summary>
    /// Types <paramref name="text"/> into the foreground app as Unicode keystrokes, leaving the
    /// clipboard completely alone.
    ///
    /// <para>Used by the layout fix when "don't touch the clipboard" is on: the text never reaches
    /// the clipboard, so it cannot appear in Windows clipboard history (Win+V), in a clipboard
    /// manager, or in cloud clipboard sync — and the user's own clipboard keeps its contents
    /// throughout.</para>
    /// </summary>
    public async Task<bool> TypeAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // Same reason as the paste path: a modifier still held from the hotkey would turn the
        // typed characters into shortcuts (or drop them).
        await VirtualKeyboard.PrepareForChordAsync(ModifierWaitMs, ct).ConfigureAwait(false);

        try
        {
            ForegroundWindow.WithForegroundFocus(() => VirtualKeyboard.TypeText(text));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Typing the text into the foreground window failed.");
            return false;
        }
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard and pastes it into the foreground app,
    /// leaving it on the clipboard afterwards so the user can paste it again themselves.
    /// Returns false if the clipboard could not be written or the paste could not be sent.
    /// </summary>
    public async Task<bool> PasteAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // SetText throws while another app holds the clipboard open, which happens in short
        // bursts (a clipboard manager, an Office paste). One retry covers the burst; failing
        // without it left the paste silently doing nothing.
        var written = await OnUiThread(() => TrySetClipboardText(text));
        if (!written)
        {
            await Task.Delay(80, ct).ConfigureAwait(false);
            written = await OnUiThread(() => TrySetClipboardText(text));
        }
        if (!written)
        {
            Log.Error("Could not set clipboard text for injection; the clipboard stayed locked.");
            return false;
        }

        // A synthesized Ctrl+V while the user still holds the hotkey's Ctrl+Shift would reach
        // the app as Shift+Ctrl+V — or, once their Ctrl lifts mid-chord, as a bare "V" that
        // types a stray character. Waiting for the physical keys to come up avoids both; a
        // modifier that is stuck or latched (Sticky Keys) is released outright, since waiting
        // for a key the user is not holding would never end.
        await VirtualKeyboard.PrepareForChordAsync(ModifierWaitMs, ct).ConfigureAwait(false);

        try
        {
            SendCtrlV();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Paste key synthesis failed.");
            return false;
        }
    }

    /// <summary>Synthesizes a Ctrl+V paste chord, with input focus attached to the foreground app's
    /// thread so it reaches the window the user is actually typing into — and so any modifier
    /// still down there is cleared against that window's own input state first.</summary>
    private static void SendCtrlV() => ForegroundWindow.WithForegroundFocus(
        () => VirtualKeyboard.ClearModifiersAndChord(VirtualKeyboard.VK_CONTROL, VirtualKeyboard.VK_V));

    /// <summary>Puts text on the clipboard, returning false instead of throwing when it is locked.</summary>
    private static bool TrySetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Clipboard.SetText failed; will retry once.");
            return false;
        }
    }

    /// <summary>Runs <paramref name="work"/> on the WPF UI (STA) thread.</summary>
    private static Task<T> OnUiThread<T>(Func<T> work)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.FromResult(work());
        return dispatcher.InvokeAsync(work).Task;
    }
}
