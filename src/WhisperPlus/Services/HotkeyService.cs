using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Serilog;
using WhisperPlus.Helpers;
using WhisperPlus.Interop;

namespace WhisperPlus.Services;

/// <summary>
/// Logical hotkey action names registered with the <see cref="HotkeyService"/>.
/// Add new values here when wiring up new features.
/// </summary>
public static class HotkeyActions
{
    public const string Dictate = "dictate";
    public const string FixLayout = "fixLayout";
    public const string RecorderStart = "recorderStart";
    public const string RecorderPause = "recorderPause";
    public const string RecorderStop = "recorderStop";
    public const string CopyLastTranscript = "copyLastTranscript";

    /// <summary>
    /// Prefix for per-engine/model dictation preset hotkeys. The preset's stable GUID id is
    /// appended after this, forming the action key registered with <see cref="HotkeyService"/>
    /// (e.g. <c>preset:ab12…</c>).
    /// </summary>
    public const string PresetPrefix = "preset:";

    /// <summary>Builds the action key for a preset id.</summary>
    public static string PresetAction(string presetId) => PresetPrefix + presetId;

    /// <summary>If <paramref name="action"/> is a preset action, returns its preset id.</summary>
    public static bool TryGetPresetId(string action, out string presetId)
    {
        if (action.StartsWith(PresetPrefix, StringComparison.Ordinal) && action.Length > PresetPrefix.Length)
        {
            presetId = action[PresetPrefix.Length..];
            return true;
        }
        presetId = "";
        return false;
    }
}

/// <summary>
/// Manages global Win32 hotkeys via RegisterHotKey on a hidden message-only window.
/// Hotkeys are keyed by a string action name (see <see cref="HotkeyActions"/>). The
/// service subscribes to WM_HOTKEY through the window's HwndSource hook and raises
/// <see cref="HotkeyPressed"/> for consumers to handle.
///
/// Thread-safety: registration/unregistration must be called on the UI thread because
/// RegisterHotKey requires it. Consumers should marshal via <see cref="Application.Current.Dispatcher"/>.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    /// <summary>Counter for unique hotkey ids passed to RegisterHotKey.</summary>
    private int _nextId;
    private readonly Dictionary<int, string> _idToAction = new();
    private readonly Dictionary<string, int> _actionToId = new();
    private readonly Dictionary<string, (uint Modifiers, uint VirtualKey)> _actionToGesture = new();
    private HwndSource? _hwndSource;
    private DispatcherTimer? _releaseTimer;
    private string? _releaseTrackingAction;
    private uint _releaseTrackingModifiers;
    private uint _releaseTrackingVirtualKey;
    private bool _disposed;

    /// <summary>Raised on the UI thread when any registered hotkey fires.</summary>
    public event Action<string>? HotkeyPressed;

    /// <summary>Raised after a tracked hotkey is fully released (used for hold-to-talk).</summary>
    public event Action<string>? HotkeyReleased;

    /// <summary>
    /// Creates a hidden Win32 message-only window (HWND_MESSAGE) that owns the hotkeys,
    /// and installs the WM_HOTKEY hook on it. Using a native message-only window (rather
    /// than a WPF <see cref="Window"/>) avoids style/chrome interactions and is cheaper.
    /// Must be called once on the UI thread before any Register call.
    /// </summary>
    public void Initialize()
    {
        if (_hwndSource is not null) return;

        var parameters = new HwndSourceParameters("WhisperPlusHotkeyHost")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            // HWND_MESSAGE: a message-only window that is not visible, not in the taskbar,
            // and cannot receive input — ideal for hosting a low-level message hook.
            ParentWindow = new IntPtr(-3),
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
        _releaseTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _releaseTimer.Tick += ReleaseTimer_Tick;
    }

    /// <summary>
    /// Registers (or re-registers) a hotkey for the given action. Returns false if the
    /// gesture string is invalid or already claimed by another app.
    /// </summary>
    public bool Register(string action, string gesture)
    {
        EnsureInitialized();
        Unregister(action); // clear any prior binding for this action

        var parsed = HotkeyGesture.Parse(gesture);
        if (parsed is null)
        {
            Log.Warning("Invalid hotkey gesture '{Gesture}' for action {Action}", gesture, action);
            return false;
        }

        var (modifiers, vk) = parsed.Value;
        var id = System.Threading.Interlocked.Increment(ref _nextId);
        var hwnd = _hwndSource?.Handle ?? IntPtr.Zero;

        if (!Win32.RegisterHotKey(hwnd, id, modifiers, vk))
        {
            Log.Warning("RegisterHotKey failed for {Action} = '{Gesture}' (error {Error})",
                action, gesture, System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return false;
        }

        _idToAction[id] = action;
        _actionToId[action] = id;
        _actionToGesture[action] = (modifiers & ~Win32.MOD_NOREPEAT, vk);
        Log.Information("Registered hotkey {Action} = '{Gesture}'", action, gesture);
        return true;
    }

    /// <summary>Unregisters a single action's hotkey (no-op if not registered).</summary>
    public void Unregister(string action)
    {
        if (!_actionToId.TryGetValue(action, out var id)) return;
        var hwnd = _hwndSource?.Handle ?? IntPtr.Zero;
        Win32.UnregisterHotKey(hwnd, id);
        _idToAction.Remove(id);
        _actionToId.Remove(action);
        _actionToGesture.Remove(action);
        if (_releaseTrackingAction == action) StopReleaseTracking();
    }

    /// <summary>Unregisters all currently bound hotkeys.</summary>
    public void UnregisterAll()
    {
        var hwnd = _hwndSource?.Handle ?? IntPtr.Zero;
        foreach (var id in _idToAction.Keys)
            Win32.UnregisterHotKey(hwnd, id);
        _idToAction.Clear();
        _actionToId.Clear();
        _actionToGesture.Clear();
        StopReleaseTracking();
    }

    /// <summary>Begins watching the physical state of a registered key combination.</summary>
    public void TrackRelease(string action)
    {
        if (!_actionToGesture.TryGetValue(action, out var gesture)) return;
        _releaseTrackingAction = action;
        _releaseTrackingModifiers = gesture.Modifiers;
        _releaseTrackingVirtualKey = gesture.VirtualKey;
        _releaseTimer?.Start();
    }

    private void ReleaseTimer_Tick(object? sender, EventArgs e)
    {
        if (_releaseTrackingAction is null || IsGestureHeld(_releaseTrackingModifiers, _releaseTrackingVirtualKey)) return;
        var action = _releaseTrackingAction;
        StopReleaseTracking();
        try { HotkeyReleased?.Invoke(action); }
        catch (Exception ex) { Log.Error(ex, "Error handling hotkey release {Action}", action); }
    }

    private void StopReleaseTracking()
    {
        _releaseTimer?.Stop();
        _releaseTrackingAction = null;
        _releaseTrackingModifiers = 0;
        _releaseTrackingVirtualKey = 0;
    }

    /// <summary>
    /// True while every key of the gesture is still physically down — the check that decides when
    /// hold-to-talk has ended.
    ///
    /// <para>Each modifier is probed with its generic code <i>and</i> its left/right variants. Some
    /// drivers and all injected input update only <c>VK_LCONTROL</c>/<c>VK_RCONTROL</c>, so a
    /// generic-only check saw a held Ctrl as released and ended the dictation mid-sentence.</para>
    /// </summary>
    private static bool IsGestureHeld(uint modifiers, uint virtualKey)
    {
        if (!Win32.IsAnyDown((int)virtualKey)) return false;
        if ((modifiers & Win32.MOD_CONTROL) != 0 && !Win32.IsAnyDown(0x11, 0xA2, 0xA3)) return false;
        if ((modifiers & Win32.MOD_ALT) != 0 && !Win32.IsAnyDown(0x12, 0xA4, 0xA5)) return false;
        if ((modifiers & Win32.MOD_SHIFT) != 0 && !Win32.IsAnyDown(0x10, 0xA0, 0xA1)) return false;
        if ((modifiers & Win32.MOD_WIN) != 0 && !Win32.IsAnyDown(0x5B, 0x5C)) return false;
        return true;
    }

    private void EnsureInitialized()
    {
        if (_hwndSource is null) Initialize();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (_idToAction.TryGetValue(id, out var action))
            {
                try { HotkeyPressed?.Invoke(action); }
                catch (Exception ex) { Log.Error(ex, "Error handling hotkey {Action}", action); }
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _hwndSource?.RemoveHook(WndProc);
        if (_releaseTimer is not null) _releaseTimer.Tick -= ReleaseTimer_Tick;
        _hwndSource = null;
    }
}
