using System;
using System.Windows;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Serilog;

namespace WhisperPlus.Services;

/// <summary>
/// Owns the system-tray notify icon and its context menu. The icon lets the user open
/// settings, show/hide the floating widgets, toggle startup, and quit. Provides a
/// <see cref="ShowSettingsRequested"/> event so <see cref="App"/> can route to the
/// main window without coupling the tray to specific windows.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TranscriptHistory _history;
    private TaskbarIcon? _trayIcon;
    private bool _disposed;

    /// <summary>Submenu of recent transcripts, rebuilt every time the menu opens.</summary>
    private System.Windows.Controls.MenuItem? _transcriptsMenu;

    /// <summary>Submenu of clipboard snapshots that a copy displaced, rebuilt on open.</summary>
    private System.Windows.Controls.MenuItem? _clipboardMenu;

    public TrayService(TranscriptHistory history)
    {
        _history = history;
    }

    /// <summary>Raised when the user picks "Open Settings" from the tray.</summary>
    public event Action? ShowSettingsRequested;

    /// <summary>Raised when the user picks "Show Dictation" from the tray.</summary>
    public event Action? ShowDictationRequested;

    /// <summary>Raised when the user picks "Show Recorder" from the tray.</summary>
    public event Action? ShowRecorderRequested;

    /// <summary>Raised when the user picks "Take Screenshot" from the tray.</summary>
    public event Action? ScreenshotRequested;

    /// <summary>Raised when the user picks "Pin from Clipboard" from the tray.</summary>
    public event Action? PinClipboardRequested;

    /// <summary>Raised when the user picks "Exit" from the tray.</summary>
    public event Action? ExitRequested;

    /// <summary>Creates and shows the tray icon. Must be called on the UI thread.</summary>
    public void Initialize()
    {
        if (_trayIcon is not null) return;

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "WhisperPlus",
            Icon = LoadAppIcon(),
            ContextMenu = BuildMenu(),
            Visibility = Visibility.Visible,
        };

        // TaskbarIcon creates its native shell icon lazily.  Do this explicitly while
        // the main window is still open so hiding that window cannot leave the app
        // running without an icon in the notification-area overflow menu.
        _trayIcon.ForceCreate(false);
        _trayIcon.TrayLeftMouseUp += (_, _) => ShowSettingsRequested?.Invoke();
    }

    /// <summary>
    /// Loads the embedded app icon (Assets/app-icon.ico) as a Win32 icon for the tray.
    /// Falls back to a generic system icon only if the resource is missing or unreadable.
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            const string packUri = "pack://application:,,,/Assets/app-icon.ico";
            var uri = new Uri(packUri);
            var resource = Application.GetResourceStream(uri);
            if (resource?.Stream is null)
            {
                Log.Warning("Tray icon resource not found at {Uri}; using fallback.", packUri);
                return System.Drawing.SystemIcons.Application;
            }
            // Icon(Stream) retains a reference to its source stream on some Windows
            // versions. Clone it before disposing the WPF resource stream; otherwise
            // the shell can silently omit the icon after the main window is hidden.
            using (resource.Stream)
            using (var source = new System.Drawing.Icon(resource.Stream))
            {
                return (System.Drawing.Icon)source.Clone();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load embedded tray icon; using fallback.");
            return System.Drawing.SystemIcons.Application;
        }
    }

    private System.Windows.Controls.ContextMenu BuildMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var itemOpen = NewItem("Open Settings", () => ShowSettingsRequested?.Invoke());
        var itemScreenshot = NewItem("Take Screenshot", () => ScreenshotRequested?.Invoke());
        var itemPinClipboard = NewItem("Pin from Clipboard", () => PinClipboardRequested?.Invoke());
        var itemDictation = NewItem("Show Dictation", () => ShowDictationRequested?.Invoke());
        var itemRecorder = NewItem("Show Recorder", () => ShowRecorderRequested?.Invoke());

        // The history and the displaced-clipboard ring live in memory, so without a menu there was
        // no way to reach either of them: Ctrl+Alt+C parked the clipboard and nothing could give it
        // back. Both submenus are rebuilt on open, which keeps them in step with the data without
        // subscribing to every change.
        _transcriptsMenu = new System.Windows.Controls.MenuItem { Header = "Recent Transcripts" };
        _clipboardMenu = new System.Windows.Controls.MenuItem { Header = "Restore Clipboard" };
        menu.Opened += (_, _) =>
        {
            PopulateHistoryMenus();
            try { itemPinClipboard.IsEnabled = Clipboard.ContainsImage(); }
            catch { itemPinClipboard.IsEnabled = true; }
        };

        var sep = new System.Windows.Controls.Separator();
        var itemExit = NewItem("Exit", () => ExitRequested?.Invoke());

        menu.Items.Add(itemOpen);
        menu.Items.Add(itemScreenshot);
        menu.Items.Add(itemPinClipboard);
        menu.Items.Add(itemDictation);
        menu.Items.Add(itemRecorder);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(_transcriptsMenu);
        menu.Items.Add(_clipboardMenu);
        menu.Items.Add(sep);
        menu.Items.Add(itemExit);
        return menu;
    }

    /// <summary>Fills both history submenus from the current state of <see cref="TranscriptHistory"/>.</summary>
    private void PopulateHistoryMenus()
    {
        if (_transcriptsMenu is null || _clipboardMenu is null) return;

        _transcriptsMenu.Items.Clear();
        var transcripts = _history.Transcripts;
        if (transcripts.Count == 0)
        {
            _transcriptsMenu.Items.Add(DisabledItem("Nothing dictated yet"));
        }
        else
        {
            foreach (var entry in transcripts)
            {
                var captured = entry;
                var item = NewItem(Preview(entry.Text), () => _ = _history.CopyAsync(captured));
                item.ToolTip = entry.Text;
                _transcriptsMenu.Items.Add(item);
            }
        }

        _clipboardMenu.Items.Clear();
        var snapshots = _history.ClipboardRing;
        if (snapshots.Count == 0)
        {
            _clipboardMenu.Items.Add(DisabledItem("Nothing displaced yet"));
        }
        else
        {
            foreach (var snapshot in snapshots)
            {
                var captured = snapshot;
                var item = NewItem(Preview(snapshot.Preview), () => _ = _history.RestoreAsync(captured));
                item.ToolTip = snapshot.Preview;
                _clipboardMenu.Items.Add(item);
            }
        }
    }

    /// <summary>One line, short enough for a menu — the full text stays in the tooltip.</summary>
    private static string Preview(string? text)
    {
        var single = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (single.Length == 0) return "(empty)";
        return single.Length <= 48 ? single : single[..47] + "…";
    }

    private static System.Windows.Controls.MenuItem DisabledItem(string header) =>
        new() { Header = header, IsEnabled = false };

    private static System.Windows.Controls.MenuItem NewItem(string header, Action onClick)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _trayIcon?.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "Error disposing tray icon."); }
        _trayIcon = null;
    }
}
