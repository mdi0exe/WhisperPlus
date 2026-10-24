using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Serilog;

namespace WhisperPlus.Services;

/// <summary>
/// A detached copy of the clipboard's contents, safe to hold on to. The live
/// <see cref="IDataObject"/> the shell hands out is owned by whichever app put it there and can
/// go stale (or start refusing reads) at any moment, so every readable format is copied into a
/// private <see cref="DataObject"/> up front.
/// </summary>
/// <param name="Data">The detached data object to put back on the clipboard.</param>
/// <param name="Text">
/// The snapshot's text, if it had any. Kept alongside <paramref name="Data"/> so callers can
/// compare contents without knowing how the data object is shaped.
/// </param>
/// <param name="Preview">Short label for menus, e.g. the first line of the text.</param>
/// <param name="CapturedUtc">When the snapshot was taken.</param>
public sealed record ClipboardSnapshot(object? Data, string? Text, string Preview, DateTime CapturedUtc)
{
    /// <summary>True when there was actually something on the clipboard to restore.</summary>
    public bool HasContent => Data is not null;
}

/// <summary>
/// The clipboard operations this app needs, behind an interface so the transcript history can be
/// tested without a desktop: <see cref="System.Windows.Clipboard"/> requires an STA thread with a
/// running WPF application, neither of which exists in a unit-test host.
/// </summary>
public interface IClipboardAccess
{
    /// <summary>Copies the current clipboard contents into a detached snapshot.</summary>
    Task<ClipboardSnapshot> CaptureAsync();

    /// <summary>Replaces the clipboard with plain text.</summary>
    Task<bool> SetTextAsync(string text);

    /// <summary>Puts a previously captured snapshot back on the clipboard.</summary>
    Task<bool> RestoreAsync(ClipboardSnapshot snapshot);

    /// <summary>
    /// Empties the clipboard. Used to restore a snapshot of an empty clipboard: a user who had
    /// nothing copied should be left with nothing copied, not with our transcript.
    /// </summary>
    Task<bool> ClearAsync();
}

/// <summary>
/// WPF implementation of <see cref="IClipboardAccess"/>. All work is marshalled to the UI (STA)
/// thread, which is the only place OLE clipboard access is allowed — the same constraint
/// <see cref="TextInjector"/> works within.
/// </summary>
public sealed class WpfClipboardAccess : IClipboardAccess
{
    /// <summary>Characters of text kept for a menu label.</summary>
    private const int PreviewLength = 48;

    public Task<ClipboardSnapshot> CaptureAsync() => OnUiThread(() =>
    {
        IDataObject? live;
        try { live = Clipboard.GetDataObject(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the clipboard.");
            return Empty();
        }
        if (live is null) return Empty();

        var copy = new DataObject();
        var copied = 0;
        string? text = null;

        string[] formats;
        try { formats = live.GetFormats(autoConvert: false); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not enumerate clipboard formats.");
            return Empty();
        }

        foreach (var format in formats)
        {
            // Formats are copied one at a time and failures skipped: a clipboard offering a
            // format it cannot actually produce must not cost us the ones it can.
            try
            {
                var data = live.GetData(format, autoConvert: false);
                if (data is null) continue;
                copy.SetData(format, data);
                copied++;
                if (text is null && data is string s && s.Length > 0) text = s;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Skipped unreadable clipboard format {Format}.", format);
            }
        }

        if (copied == 0) return Empty();
        return new ClipboardSnapshot(copy, text, BuildPreview(text, formats), DateTime.UtcNow);
    });

    public Task<bool> SetTextAsync(string text) => OnUiThread(() =>
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not set clipboard text.");
            return false;
        }
    });

    public Task<bool> RestoreAsync(ClipboardSnapshot snapshot) => OnUiThread(() =>
    {
        if (snapshot.Data is not IDataObject data) return false;
        try
        {
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not restore the clipboard snapshot.");
            return false;
        }
    });

    public Task<bool> ClearAsync() => OnUiThread(() =>
    {
        try
        {
            Clipboard.Clear();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not clear the clipboard.");
            return false;
        }
    });

    private static ClipboardSnapshot Empty() => new(null, null, "(empty)", DateTime.UtcNow);

    /// <summary>Builds a one-line menu label from the snapshot's text, or its formats.</summary>
    private static string BuildPreview(string? text, IReadOnlyCollection<string> formats)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (single.Contains("  ", StringComparison.Ordinal))
                single = single.Replace("  ", " ", StringComparison.Ordinal);
            return single.Length <= PreviewLength ? single : single[..PreviewLength] + "…";
        }

        return formats.Count switch
        {
            0 => "(empty)",
            1 => $"({FirstOf(formats)})",
            _ => $"({FirstOf(formats)} + {formats.Count - 1} more)",
        };

        static string FirstOf(IEnumerable<string> items)
        {
            foreach (var item in items) return item;
            return "unknown";
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the WPF UI thread. Falls back to running inline when there
    /// is no application (which only happens outside the running app).
    /// </summary>
    private static Task<T> OnUiThread<T>(Func<T> work)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.FromResult(work());
        return dispatcher.InvokeAsync(work).Task;
    }
}
