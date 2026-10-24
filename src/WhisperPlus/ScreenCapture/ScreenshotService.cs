using System;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using WhisperPlus.Services;

namespace WhisperPlus.ScreenCapture;

/// <summary>
/// Owns the screenshot feature: the global shortcut, the capture itself, and the single
/// annotation overlay that may be open at a time.
///
/// <para><b>Memory.</b> Nothing here is created until the first capture. The SkiaSharp drawing
/// stack is only touched when <see cref="CaptureAsync"/> constructs an overlay, so a session that
/// never takes a screenshot never loads it; the overlay releases its screen-sized buffers on
/// close (see <see cref="CaptureOverlayWindow.Dispose"/>). That keeps the app's idle working set
/// where it was before the feature existed.</para>
/// </summary>
public sealed class ScreenshotService : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ToolStyleMemory _style = new();
    private readonly ScreenshotHotkeyHook _hook = new();

    private CaptureOverlayWindow? _overlay;
    private bool _capturing;
    private bool _disposed;

    public ScreenshotService(ISettingsService settings)
    {
        _settings = settings;
        _hook.Pressed += OnHotkeyPressed;
        _settings.Changed += (_, _) => ApplySettings();
    }

    /// <summary>Hides the app's own floating widgets so they do not appear in the capture.</summary>
    public Action? BeforeCapture { get; set; }

    /// <summary>Restores whatever <see cref="BeforeCapture"/> hid, once the overlay has closed.</summary>
    public Action? AfterCapture { get; set; }

    /// <summary>True while an overlay is open or a capture is in flight.</summary>
    public bool IsCapturing => _capturing;

    /// <summary>Installs the keyboard hook and applies the stored gesture.</summary>
    public void Initialize()
    {
        _hook.Install();
        ApplySettings();
        Log.Information("Screenshot hotkey '{Gesture}' (enabled={Enabled}, swallow={Swallow})",
            _settings.Current.HotkeyScreenshot, _hook.Enabled, _hook.SwallowKeystroke);
    }

    /// <summary>Re-reads the gesture, the enabled flag and the swallow behaviour from settings.</summary>
    public void ApplySettings()
    {
        var s = _settings.Current;
        _hook.SetGesture(s.HotkeyScreenshot);
        _hook.SwallowKeystroke = s.ScreenshotReplaceWindowsSnipping;
        _hook.Enabled = s.ScreenshotEnabled && !_hotkeySuspended;
    }

    private bool _hotkeySuspended;

    /// <summary>
    /// Temporarily stops the shortcut from firing. Used while the settings page is capturing a
    /// new combination: if the hook kept consuming Win+Shift+S, the rebind button would never
    /// see the keystroke the user is trying to assign.
    /// </summary>
    public void SuspendHotkey(bool suspended)
    {
        _hotkeySuspended = suspended;
        ApplySettings();
    }

    private void OnHotkeyPressed()
    {
        // The hook callback must return immediately: it runs on the same thread that delivers
        // every keystroke system-wide, so building a window inside it would stutter typing.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = CaptureAsync()));
    }

    /// <summary>Starts a capture, or brings the open overlay forward.</summary>
    public async Task CaptureAsync()
    {
        if (_disposed) return;
        if (_overlay != null)
        {
            _overlay.Activate();
            return;
        }
        if (_capturing) return;
        _capturing = true;
        Log.Debug("Screenshot capture requested");

        try
        {
            BeforeCapture?.Invoke();
            // Give the compositor a frame or two to actually remove the hidden windows before
            // the desktop is frozen; otherwise they appear in the screenshot.
            await Task.Delay(120);

            var bounds = ScreenCaptureCore.VirtualScreenBounds;
            var screenshot = ScreenCaptureCore.CaptureVirtualScreen(includeCursor: false);
            Log.Debug("Captured virtual screen {Width}x{Height} at {X},{Y}",
                bounds.Width, bounds.Height, bounds.X, bounds.Y);

            var s = _settings.Current;
            var overlay = new CaptureOverlayWindow(screenshot, bounds, _style, CaptureMode.Region)
            {
                SaveDirectory = string.IsNullOrWhiteSpace(s.ScreenshotSaveDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                    : s.ScreenshotSaveDirectory,
                OutputFormat = s.ScreenshotFormat,
                Quality = s.ScreenshotQuality,
            };
            overlay.SaveDirectoryChanged += dir => _settings.Update(cfg => cfg.ScreenshotSaveDirectory = dir);
            overlay.SessionEnded += OnSessionEnded;

            _overlay = overlay;
            overlay.Show();
            overlay.Activate();
            Log.Information("Screenshot overlay opened ({Mode})", CaptureMode.Region);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Screen capture failed");
            _capturing = false;
            _overlay = null;
            AfterCapture?.Invoke();
        }
    }

    private void OnSessionEnded()
    {
        if (_overlay != null)
        {
            _overlay.SessionEnded -= OnSessionEnded;
            _overlay = null;
        }
        _capturing = false;
        AfterCapture?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hook.Pressed -= OnHotkeyPressed;
        _hook.Dispose();
        if (_overlay != null)
        {
            _overlay.SessionEnded -= OnSessionEnded;
            _overlay.Close();
            _overlay = null;
        }
    }
}
