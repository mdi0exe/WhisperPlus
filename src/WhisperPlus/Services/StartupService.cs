using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;
using Serilog;

namespace WhisperPlus.Services;

/// <summary>
/// Toggles "launch on Windows startup" by writing/removing a value in the current
/// user's Run registry key (HKCU\Software\Microsoft\Windows\CurrentVersion\Run).
/// Per-user (no admin required). The registered value points at the running .exe and
/// passes <see cref="MinimizedSwitch"/> so a Windows-triggered launch stays in the tray
/// instead of popping the settings window in the user's face.
/// </summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WhisperPlus";

    /// <summary>Command-line switch that means "start hidden in the tray".</summary>
    public const string MinimizedSwitch = "--minimized";

    /// <summary>
    /// Command-line switch that opens a capture immediately and then stays in the tray. Useful
    /// for binding a screenshot key in another launcher, and for verifying the overlay from a
    /// script without simulating the global shortcut.
    /// </summary>
    public const string CaptureSwitch = "--capture";

    /// <summary>
    /// True if the process was launched with the minimized switch (i.e. by the Run entry, or
    /// by anyone passing it explicitly). Accepts the <c>--</c>, <c>-</c> and <c>/</c> forms.
    /// </summary>
    public static bool WasLaunchedMinimized(string[]? args) =>
        args is not null && args.Any(a =>
            a.TrimStart('-', '/').Equals("minimized", StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the process was launched with <see cref="CaptureSwitch"/>.</summary>
    public static bool WasLaunchedForCapture(string[]? args) =>
        args is not null && args.Any(a =>
            a.TrimStart('-', '/').Equals("capture", StringComparison.OrdinalIgnoreCase));

    /// <summary>True if a Run entry for this app currently exists.</summary>
    public bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }

    /// <summary>Creates the Run entry pointing at the current executable. Returns success.</summary>
    public bool Enable()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
            if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Cannot enable startup: process path is not an exe ({Path})", exePath);
                return false;
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.SetValue(ValueName, $"\"{exePath}\" {MinimizedSwitch}");
            Log.Information("Startup entry enabled: {Path}", exePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enable startup.");
            return false;
        }
    }

    /// <summary>Removes the Run entry if present. Returns success (true if nothing left).</summary>
    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Information("Startup entry disabled.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disable startup.");
            return false;
        }
    }

    /// <summary>Atomically syncs the Run entry to the desired state.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled) Enable(); else Disable();
    }
}
