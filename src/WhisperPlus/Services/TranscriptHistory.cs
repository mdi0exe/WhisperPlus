using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;

namespace WhisperPlus.Services;

/// <summary>One past dictation result.</summary>
/// <param name="Text">The transcript, exactly as it was pasted.</param>
/// <param name="LanguageCode">The 2-letter code it was transcribed as, if routing knew it.</param>
/// <param name="CreatedUtc">When the session finished.</param>
public sealed record TranscriptEntry(string Text, string? LanguageCode, DateTime CreatedUtc);

/// <summary>
/// Remembers recent transcripts so the last one can be put back on the clipboard on demand, and
/// keeps whatever the clipboard held before that so nothing the user had copied is lost.
///
/// <para>Copying inherently overwrites the clipboard — there is only one — so instead of pretending
/// otherwise, the previous contents are captured into a ring first (<see cref="ClipboardRing"/>)
/// and can be put back from the tray menu. All formats are preserved, not just text, so a copied
/// file or image survives the round trip.</para>
///
/// <para>Thread-safe: the widget adds entries from a background pump while the tray menu and
/// hotkey read them on the UI thread.</para>
/// </summary>
public sealed class TranscriptHistory
{
    /// <summary>How many transcripts are kept. Enough to find one, few enough to stay a menu.</summary>
    public const int MaxTranscripts = 25;

    /// <summary>How many displaced clipboard snapshots are kept for restoring.</summary>
    public const int MaxClipboardSnapshots = 10;

    private readonly IClipboardAccess _clipboard;
    private readonly object _gate = new();
    private readonly List<TranscriptEntry> _transcripts = new();
    private readonly List<ClipboardSnapshot> _clipboardRing = new();

    public TranscriptHistory(IClipboardAccess clipboard)
    {
        _clipboard = clipboard;
    }

    /// <summary>Raised whenever the transcript list or the clipboard ring changes.</summary>
    public event Action? Changed;

    /// <summary>Recent transcripts, newest first.</summary>
    public IReadOnlyList<TranscriptEntry> Transcripts
    {
        get { lock (_gate) return _transcripts.ToArray(); }
    }

    /// <summary>Clipboard contents displaced by a copy, newest first.</summary>
    public IReadOnlyList<ClipboardSnapshot> ClipboardRing
    {
        get { lock (_gate) return _clipboardRing.ToArray(); }
    }

    /// <summary>The most recent transcript, or null if nothing has been dictated yet.</summary>
    public TranscriptEntry? Last
    {
        get { lock (_gate) return _transcripts.Count > 0 ? _transcripts[0] : null; }
    }

    /// <summary>Records a finished transcript. Blank text is ignored.</summary>
    public void Add(string text, string? languageCode)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        lock (_gate)
        {
            _transcripts.Insert(0, new TranscriptEntry(trimmed, languageCode, DateTime.UtcNow));
            if (_transcripts.Count > MaxTranscripts) _transcripts.RemoveRange(MaxTranscripts, _transcripts.Count - MaxTranscripts);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Puts the most recent transcript on the clipboard, first parking whatever was there into
    /// <see cref="ClipboardRing"/> so it can be restored. Returns false when there is no
    /// transcript yet or the clipboard could not be written.
    /// </summary>
    public async Task<bool> CopyLastAsync()
    {
        var last = Last;
        if (last is null)
        {
            Log.Information("Copy-last-transcript requested with no transcript recorded yet.");
            return false;
        }

        // Capture before overwriting — that ordering is the whole point.
        var displaced = await _clipboard.CaptureAsync().ConfigureAwait(false);
        if (displaced.HasContent && !IsSameText(displaced, last.Text)) Push(displaced);

        var ok = await _clipboard.SetTextAsync(last.Text).ConfigureAwait(false);
        if (ok) Log.Information("Copied the last transcript ({Chars} chars) to the clipboard.", last.Text.Length);
        return ok;
    }

    /// <summary>
    /// Puts one specific transcript back on the clipboard, parking whatever is there now into
    /// <see cref="ClipboardRing"/> first — the same contract as <see cref="CopyLastAsync"/>, for the
    /// tray's history menu.
    /// </summary>
    public async Task<bool> CopyAsync(TranscriptEntry entry)
    {
        if (entry is null) return false;

        var displaced = await _clipboard.CaptureAsync().ConfigureAwait(false);
        if (displaced.HasContent && !IsSameText(displaced, entry.Text)) Push(displaced);

        var ok = await _clipboard.SetTextAsync(entry.Text).ConfigureAwait(false);
        if (ok) Log.Information("Copied a transcript from the history ({Chars} chars).", entry.Text.Length);
        return ok;
    }

    /// <summary>Puts a displaced snapshot back on the clipboard and drops it from the ring.</summary>
    public async Task<bool> RestoreAsync(ClipboardSnapshot snapshot)
    {
        var ok = await _clipboard.RestoreAsync(snapshot).ConfigureAwait(false);
        if (!ok) return false;

        lock (_gate) _clipboardRing.Remove(snapshot);
        Changed?.Invoke();
        return true;
    }

    private void Push(ClipboardSnapshot snapshot)
    {
        lock (_gate)
        {
            _clipboardRing.Insert(0, snapshot);
            if (_clipboardRing.Count > MaxClipboardSnapshots)
                _clipboardRing.RemoveRange(MaxClipboardSnapshots, _clipboardRing.Count - MaxClipboardSnapshots);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// True when the clipboard already holds exactly this transcript — pressing the hotkey twice
    /// should not fill the ring with copies of the transcript it just put there.
    /// </summary>
    private static bool IsSameText(ClipboardSnapshot snapshot, string text) =>
        snapshot.Text is { } existing &&
        string.Equals(existing.Trim(), text.Trim(), StringComparison.Ordinal);
}
