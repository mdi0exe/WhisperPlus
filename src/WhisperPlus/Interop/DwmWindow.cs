using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Serilog;

namespace WhisperPlus.Interop;

/// <summary>
/// Applies Windows 11 glass effects (Mica/Acrylic backdrop) and rounded corners to a
/// WPF window via DWM interop. Call <see cref="Apply"/> after the window source has
/// been created (e.g. from a SourceInitialized handler). On older Windows builds the
/// DWM calls simply no-op (return non-zero) and the window keeps its normal chrome.
/// </summary>
public static class DwmWindow
{
    /// <summary>Window backdrop styles, mapping to DWM DWMSBT_* values.</summary>
    public enum Backdrop
    {
        None = Win32.DWMSBT_NONE,
        Mica = Win32.DWMSBT_MAINWINDOW,
        Acrylic = Win32.DWMSBT_TRANSIENTWINDOW,
        Tabbed = Win32.DWMSBT_TABBEDWINDOW,
        Auto = Win32.DWMSBT_AUTO,
    }

    /// <summary>
    /// Enables the requested backdrop on the window, rounds its corners, and syncs the
    /// dark-mode title-bar attribute. Safe to call on any Windows version.
    /// </summary>
    public static void Apply(Window window, Backdrop backdrop = Backdrop.Mica, bool darkMode = true)
    {
        if (window is null) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            // Defer until the HWND exists — once. A fresh lambda per call was never unsubscribed,
            // so calling Apply before the handle existed stacked up handlers that all fired later
            // (and kept the window alive). The handler is removed again when it runs.
            EventHandler? deferred = null;
            deferred = (_, _) =>
            {
                window.SourceInitialized -= deferred;
                ApplyInternal(window, backdrop, darkMode);
            };
            window.SourceInitialized += deferred;
            return;
        }
        ApplyInternal(window, backdrop, darkMode);
    }

    private static void ApplyInternal(Window window, Backdrop backdrop, bool darkMode)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            // Dark-mode title bar (Win10 1809+ / Win11).
            int dark = darkMode ? 1 : 0;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            // Rounded corners (Win11).
            int corner = Win32.DWMWCP_ROUND;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            // System backdrop (Win11 22H2+). Requires the window to extend into the client area
            // (typically via WindowStyle=None or WPF-UI's BackdropType).
            int backdropValue = (int)backdrop;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropValue, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DWM backdrop attributes could not be applied.");
        }
    }

    /// <summary>
    /// Returns true if the OS is Windows 11 22H2 or newer (build &gt;= 22621) — the first build with
    /// <c>DWMWA_SYSTEMBACKDROP_TYPE</c>, which is what the Mica/Acrylic backdrop needs.
    /// </summary>
    public static bool IsWindows11OrGreater()
    {
        try
        {
            return Environment.OSVersion.Version.Major > 10 ||
                   (Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Build >= 22621);
        }
        catch
        {
            return false;
        }
    }
}
