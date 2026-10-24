using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WhisperPlus.Services;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for <see cref="TranscriptHistory"/>: the cap and ordering of the transcript ring, and
/// the headline guarantee of the copy-last-transcript feature — that whatever was on the
/// clipboard first lands in the restore ring <em>before</em> it is overwritten. All clipboard
/// access goes through <see cref="FakeClipboardAccess"/>, since System.Windows.Clipboard needs a
/// running STA WPF app and is unusable headlessly.
/// </summary>
public class TranscriptHistoryTests
{
    private static TranscriptHistory NewHistory(FakeClipboardAccess? clipboard = null) =>
        new(clipboard ?? new FakeClipboardAccess());

    // ---- Add / ordering / cap ----

    [Fact]
    public void Add_Keeps_Newest_First()
    {
        var history = NewHistory();

        history.Add("first", "fa");
        history.Add("second", "en");
        history.Add("third", null);

        var list = history.Transcripts;
        Assert.Equal("third", list[0].Text);
        Assert.Equal("second", list[1].Text);
        Assert.Equal("first", list[2].Text);
        Assert.Null(list[0].LanguageCode);
    }

    // ---- Copying one specific entry (the tray's history menu) ----

    [Fact]
    public async Task CopyAsync_PutsTheChosenEntryOnTheClipboard()
    {
        var clipboard = new FakeClipboardAccess();
        var history = NewHistory(clipboard);
        history.Add("older", "fa");
        history.Add("newer", "en");

        var older = history.Transcripts[1];
        Assert.True(await history.CopyAsync(older));

        Assert.Equal("older", clipboard.CurrentText);
    }

    [Fact]
    public async Task CopyAsync_ParksThePreviousClipboardFirst()
    {
        var clipboard = new FakeClipboardAccess();
        await clipboard.SetTextAsync("was here before");
        var history = NewHistory(clipboard);
        history.Add("transcript", "fa");

        Assert.Empty(history.ClipboardRing);
        Assert.True(await history.CopyAsync(history.Transcripts[0]));

        // The displaced text is restorable — that is the whole point of the ring.
        Assert.Single(history.ClipboardRing);
        Assert.Equal("was here before", history.ClipboardRing[0].Text);
    }

    [Fact]
    public void Add_Ignores_Blank_Text()
    {
        var history = NewHistory();

        history.Add("", "fa");
        history.Add("   ", "en");

        Assert.Empty(history.Transcripts);
        Assert.Null(history.Last);
    }

    [Fact]
    public void Add_Caps_At_Maximum()
    {
        var history = NewHistory();

        for (var i = 0; i < TranscriptHistory.MaxTranscripts + 10; i++)
            history.Add($"item {i}", null);

        Assert.Equal(TranscriptHistory.MaxTranscripts, history.Transcripts.Count);
        // The newest ten overran the cap and the oldest were evicted; item 0 is the oldest survivor.
        Assert.Equal($"item {10}", history.Transcripts[^1].Text);
        Assert.Equal($"item {TranscriptHistory.MaxTranscripts + 9}", history.Transcripts[0].Text);
    }

    [Fact]
    public void Last_Returns_Most_Recent()
    {
        var history = NewHistory();

        history.Add("only", "en");

        var last = history.Last;
        Assert.NotNull(last);
        Assert.Equal("only", last!.Text);
    }

    // ---- CopyLastAsync: the clipboard-preservation guarantee ----

    /// <summary>
    /// The core contract of item 7: pressing the copy-last-transcript hotkey must not destroy the
    /// clipboard. The previous contents are captured and pushed onto the restore ring <em>before</em>
    /// the transcript overwrites them.
    /// </summary>
    [Fact]
    public async Task CopyLast_Parks_Previous_Clipboard_Before_Overwriting()
    {
        var clipboard = new FakeClipboardAccess(currentText: "my copied text");
        var history = NewHistory(clipboard);
        history.Add("the transcript", "fa");

        var ok = await history.CopyLastAsync();

        Assert.True(ok);
        // The clipboard now holds the transcript...
        Assert.Equal("the transcript", clipboard.CurrentText);
        // ...and the displaced text survived in the restore ring.
        Assert.Single(history.ClipboardRing);
        Assert.Equal("my copied text", history.ClipboardRing[0].Text);
    }

    [Fact]
    public async Task CopyLast_Returns_False_When_Nothing_Recorded()
    {
        var history = NewHistory();

        var ok = await history.CopyLastAsync();

        Assert.False(ok);
        Assert.Empty(history.ClipboardRing);
    }

    /// <summary>
    /// Pressing the hotkey twice should not fill the ring with copies of the transcript the first
    /// press just wrote. Once the clipboard already holds this transcript, the displaced snapshot
    /// matches it and is dropped instead of parked.
    /// </summary>
    [Fact]
    public async Task CopyLast_Twice_Does_Not_Ring_The_Same_Text()
    {
        var clipboard = new FakeClipboardAccess(currentText: "unrelated");
        var history = NewHistory(clipboard);
        history.Add("same transcript", "en");

        await history.CopyLastAsync();   // parks "unrelated"
        await history.CopyLastAsync();   // clipboard now holds the transcript → not parked again

        Assert.Single(history.ClipboardRing);
        Assert.Equal("unrelated", history.ClipboardRing[0].Text);
    }

    [Fact]
    public async Task CopyLast_Does_Not_Ring_Empty_Clipboard()
    {
        var clipboard = new FakeClipboardAccess(currentText: null);   // empty clipboard
        var history = NewHistory(clipboard);
        history.Add("transcript", "fa");

        await history.CopyLastAsync();

        Assert.Empty(history.ClipboardRing);
        Assert.Equal("transcript", clipboard.CurrentText);
    }

    [Fact]
    public async Task Clipboard_Ring_Caps_At_Maximum()
    {
        var clipboard = new FakeClipboardAccess(currentText: "seed");
        var history = NewHistory(clipboard);
        history.Add("transcript", null);

        // Each round: change what's on the clipboard, then copy-last parks it.
        for (var i = 0; i < TranscriptHistory.MaxClipboardSnapshots + 5; i++)
        {
            clipboard.CurrentText = $"clip {i}";
            await history.CopyLastAsync();
        }

        Assert.Equal(TranscriptHistory.MaxClipboardSnapshots, history.ClipboardRing.Count);
    }

    // ---- RestoreAsync ----

    [Fact]
    public async Task Restore_Puts_Snapshot_Back_And_Removes_It_From_Ring()
    {
        var clipboard = new FakeClipboardAccess(currentText: "original");
        var history = NewHistory(clipboard);
        history.Add("transcript", "fa");
        await history.CopyLastAsync();

        var snapshot = history.ClipboardRing[0];
        var ok = await history.RestoreAsync(snapshot);

        Assert.True(ok);
        Assert.Empty(history.ClipboardRing);
        Assert.Equal("original", clipboard.CurrentText);
    }

    /// <summary>
    /// <see cref="TranscriptHistory.Changed"/> fires on add, copy (which mutates the ring), and
    /// restore — the tray menu rebuilds on it, so a missing raise would leave a stale menu. The
    /// clipboard is seeded with content so the copy actually parks a snapshot (a no-op copy raises
    /// nothing, by design).
    /// </summary>
    [Fact]
    public async Task Changed_Raises_On_Each_Mutation()
    {
        var clipboard = new FakeClipboardAccess(currentText: "something to displace");
        var history = NewHistory(clipboard);
        var raises = 0;
        history.Changed += () => raises++;

        history.Add("one", "en");
        Assert.Equal(1, raises);

        await history.CopyLastAsync();   // parks "something to displace" → ring changed
        Assert.Equal(2, raises);

        Assert.NotEmpty(history.ClipboardRing);
        await history.RestoreAsync(history.ClipboardRing[0]);
        Assert.Equal(3, raises);
    }
}

/// <summary>
/// In-memory <see cref="IClipboardAccess"/>. Holds one current text value and a map of snapshots
/// the test "captured", which <see cref="RestoreAsync"/> can replay. No thread marshalling, no OLE
/// — just enough to exercise <see cref="TranscriptHistory"/>'s capture-then-overwrite ordering.
/// </summary>
public sealed class FakeClipboardAccess : IClipboardAccess
{
    private string? _currentText;

    public FakeClipboardAccess(string? currentText = null) => _currentText = currentText;

    /// <summary>What <see cref="SetTextAsync"/> last wrote (or what a test pre-seeds it with).</summary>
    public string? CurrentText
    {
        get => _currentText;
        set => _currentText = value;
    }

    public Task<ClipboardSnapshot> CaptureAsync()
    {
        if (string.IsNullOrEmpty(_currentText))
        {
            return Task.FromResult(new ClipboardSnapshot(null, null, "(empty)", DateTime.UtcNow));
        }
        var data = _currentText;   // capture by value so later writes don't mutate this snapshot
        return Task.FromResult(new ClipboardSnapshot(data, data, data, DateTime.UtcNow));
    }

    public Task<bool> SetTextAsync(string text)
    {
        _currentText = text;
        return Task.FromResult(true);
    }

    public Task<bool> RestoreAsync(ClipboardSnapshot snapshot)
    {
        if (!snapshot.HasContent) return Task.FromResult(false);
        // The fake stored the text directly in Data (see CaptureAsync).
        _currentText = snapshot.Text;
        return Task.FromResult(true);
    }

    public Task<bool> ClearAsync()
    {
        _currentText = null;
        return Task.FromResult(true);
    }
}
