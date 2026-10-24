using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WhisperPlus.Helpers;
using WhisperPlus.Services;
using WhisperPlus.Models;

namespace WhisperPlus.Views.Pages;

/// <summary>
/// Hotkey rebinding UI. Each row is a button showing the current binding; clicking it
/// enters "capture" mode, where the next real keystroke (with modifiers) is captured,
/// parsed via <see cref="HotkeyGesture"/>, validated, saved to settings, and re-registered
/// globally. Press Esc to cancel without changing the binding.
/// </summary>
public partial class HotkeysPage
{
    private readonly ISettingsService _settings;
    private readonly HotkeyService _hotkeys;
    private readonly ScreenCapture.ScreenshotService _screenshots;
    private ButtonBase? _capturing;
    private bool _loading;

    public HotkeysPage(ISettingsService settings, HotkeyService hotkeys, ScreenCapture.ScreenshotService screenshots)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _screenshots = screenshots;
        InitializeComponent();
        DataContext = _settings.Current;
        _loading = true;
        DictationModeToggle.IsChecked = _settings.Current.DictationHoldToTalk;
        ScreenshotEnabledToggle.IsChecked = _settings.Current.ScreenshotEnabled;
        ScreenshotReplaceToggle.IsChecked = _settings.Current.ScreenshotReplaceWindowsSnipping;
        SelectPersianLayout(_settings.Current.PersianLayout);
        ClipboardFreeToggle.IsChecked = _settings.Current.LayoutFixClipboardFree;
        SwitchLanguageToggle.IsChecked = _settings.Current.LayoutFixSwitchLanguage;
        _loading = false;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        _screenshots.SuspendHotkey(false);
    }

    private void ClipboardFreeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s.LayoutFixClipboardFree = ClipboardFreeToggle.IsChecked == true);
    }

    private void SwitchLanguageToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s.LayoutFixSwitchLanguage = SwitchLanguageToggle.IsChecked == true);
    }

    /// <summary>Checks the combo entry matching the stored layout id.</summary>
    private void SelectPersianLayout(string? stored)
    {
        var normalized = PersianLayoutVariants.Normalize(stored);
        foreach (var item in PersianLayoutCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, normalized, StringComparison.OrdinalIgnoreCase))
            {
                PersianLayoutCombo.SelectedItem = item;
                return;
            }
        }
        PersianLayoutCombo.SelectedIndex = 0;
    }

    private void PersianLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var value = (PersianLayoutCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        _settings.Update(s => s.PersianLayout = PersianLayoutVariants.Normalize(value));
    }

    private void ScreenshotEnabledToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s.ScreenshotEnabled = ScreenshotEnabledToggle.IsChecked == true);
        _screenshots.ApplySettings();
    }

    private void ScreenshotReplaceToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s.ScreenshotReplaceWindowsSnipping = ScreenshotReplaceToggle.IsChecked == true);
        _screenshots.ApplySettings();
    }

    /// <summary>The settings property name bound to each button (resolved by tag-less convention).</summary>
    private void Rebind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button) return;
        if (_capturing == button)
        {
            CancelCapture();
            return;
        }

        _capturing = button;
        // While the screenshot shortcut is being captured the hook must not consume the
        // keystroke, or the button would never see Win+Shift+S being pressed.
        if (button.Name == nameof(BtnScreenshot)) _screenshots.SuspendHotkey(true);
        button.Content = "Press keys…  (Esc to cancel)";
        button.PreviewKeyDown += Capture_PreviewKeyDown;
        button.Focus();
    }

    private void Capture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (sender is not ButtonBase button) return;

        // Esc cancels; Windows RegisterHotKey requires a non-modifier key.
        if (e.Key == Key.Escape) { CancelCapture(); return; }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            button.Content = "Add a key, e.g. Ctrl+Shift+D";
            return;
        }

        // Build the gesture string from the WPF key + active modifiers.
        var wpfKey = e.Key == Key.System ? e.SystemKey : e.Key;
        var displayKey = KeyToString(wpfKey);
        if (displayKey is null) { CancelCapture(); return; }

        var sb = new System.Text.StringBuilder();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) sb.Append("Ctrl+");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) sb.Append("Alt+");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) sb.Append("Shift+");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) sb.Append("Win+");
        sb.Append(displayKey);

        var gesture = sb.ToString();
        if (HotkeyGesture.Parse(gesture) is null)
        {
            button.Content = "Invalid — try again";
            return;
        }

        // Stay in capture mode when the gesture was refused, so the user can simply press another
        // combination instead of clicking the button again — and so the refusal message is replaced
        // by the next attempt rather than sticking on the button until the page is rebuilt.
        if (!ApplyBinding(button, gesture)) return;
        EndCapture(button);
    }

    /// <summary>Checks whether <paramref name="gesture"/> is already assigned to another action or model preset.</summary>
    private bool IsGestureInUse(string gesture, string? currentProperty)
    {
        var s = _settings.Current;
        var hotkeyProps = new[]
        {
            nameof(AppSettings.HotkeyDictate),
            nameof(AppSettings.HotkeyFixLayout),
            nameof(AppSettings.HotkeyRecorderStart),
            nameof(AppSettings.HotkeyRecorderPause),
            nameof(AppSettings.HotkeyRecorderStop),
            nameof(AppSettings.HotkeyCopyLastTranscript),
            nameof(AppSettings.HotkeyScreenshot),
        };

        foreach (var propName in hotkeyProps)
        {
            if (propName == currentProperty) continue;
            var val = typeof(AppSettings).GetProperty(propName)?.GetValue(s) as string;
            if (string.Equals(val, gesture, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var p in s.DictationPresets)
        {
            if (string.Equals(p.Hotkey, gesture, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Persists the gesture for the button's action and re-registers the hotkey. Returns false when
    /// the gesture was refused — the button label then carries the reason and the caller keeps the
    /// capture session open for another try.
    /// </summary>
    private bool ApplyBinding(ButtonBase button, string gesture)
    {
        string? targetProp = button.Name == nameof(BtnScreenshot)
            ? nameof(AppSettings.HotkeyScreenshot)
            : ButtonToAction(button).Property;

        if (IsGestureInUse(gesture, targetProp))
        {
            button.Content = "In use — try another";
            return false;
        }

        // The screenshot shortcut is owned by the low-level keyboard hook, not by RegisterHotKey:
        // Win+Shift+S is claimed by the shell and RegisterHotKey cannot take it away. So it is
        // stored and pushed to the hook instead of being registered here.
        if (button.Name == nameof(BtnScreenshot))
        {
            _settings.Update(s => s.HotkeyScreenshot = gesture);
            _screenshots.ApplySettings();
            button.Content = gesture;
            return true;
        }

        var (action, property) = ButtonToAction(button);
        var oldGesture = _settings.Current.GetType().GetProperty(property)?.GetValue(_settings.Current) as string ?? "";

        // If another app owns the requested combination, restore the old hotkey.
        _hotkeys.Unregister(action);
        if (!_hotkeys.Register(action, gesture))
        {
            _hotkeys.Register(action, oldGesture);
            button.Content = "Unavailable - try another";
            return false;
        }

        _settings.Update(s => s.GetType().GetProperty(property)?.SetValue(s, gesture));

        button.Content = gesture;
        return true;
    }

    private void DictationModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s.DictationHoldToTalk = DictationModeToggle.IsChecked == true);
    }

    private void CancelCapture()
    {
        if (_capturing is ButtonBase button)
        {
            // Restore the button label from settings.
            var (_, property) = ButtonToAction(button);
            var value = string.IsNullOrEmpty(property)
                ? _settings.Current.HotkeyScreenshot
                : _settings.Current.GetType().GetProperty(property)?.GetValue(_settings.Current) as string;
            button.Content = value ?? "—";
            EndCapture(button);
        }
    }

    private void EndCapture(ButtonBase button)
    {
        button.PreviewKeyDown -= Capture_PreviewKeyDown;
        if (button.Name == nameof(BtnScreenshot)) _screenshots.SuspendHotkey(false);
        _capturing = null;
    }

    /// <summary>Maps a rebind button to its (action name, settings property name).</summary>
    private (string Action, string Property) ButtonToAction(ButtonBase button) => button.Name switch
    {
        nameof(BtnDictate)        => (HotkeyActions.Dictate,        nameof(_settings.Current.HotkeyDictate)),
        nameof(BtnFixLayout)      => (HotkeyActions.FixLayout,      nameof(_settings.Current.HotkeyFixLayout)),
        nameof(BtnRecorderStart)  => (HotkeyActions.RecorderStart,  nameof(_settings.Current.HotkeyRecorderStart)),
        nameof(BtnRecorderPause)  => (HotkeyActions.RecorderPause,  nameof(_settings.Current.HotkeyRecorderPause)),
        nameof(BtnRecorderStop)   => (HotkeyActions.RecorderStop,   nameof(_settings.Current.HotkeyRecorderStop)),
        nameof(BtnCopyLast)       => (HotkeyActions.CopyLastTranscript, nameof(_settings.Current.HotkeyCopyLastTranscript)),
        _ => ("", ""),
    };

    /// <summary>Converts a WPF <see cref="Key"/> to the display string used in a gesture.</summary>
    private static string? KeyToString(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Oem3 => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.Oem5 => "\\",
        Key.Oem1 => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        _ => null,
    };
}
