using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WhisperPlus.Models;
using WhisperPlus.Services;
using Wpf.Ui.Controls;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace WhisperPlus.Views.Pages;

/// <summary>
/// General settings: theme, the one app color, launch-on-startup, minimize-to-tray, and the
/// Mica backdrop toggle. Each control persists immediately via <see cref="ISettingsService"/>
/// and applies the change live so the user sees the effect without restarting.
/// </summary>
public partial class GeneralPage
{
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;
    private readonly StartupService _startup;
    private bool _loading;

    /// <summary>
    /// The color swatches offered, in palette order. One palette drives everything: the
    /// accent (buttons, toggles, highlights) and the background wash.
    /// </summary>
    private static readonly string[] Palette =
    {
        "#6759FF", // indigo (default)
        "#0078D4", // blue
        "#0099BC", // teal
        "#107C10", // green
        "#FFB900", // gold
        "#FF8C00", // orange
        "#E3008C", // magenta
        "#C239B3", // purple
        "#F1707B", // coral
        "#5D5A6B", // slate
    };

    /// <summary>The built swatches, so selection can be re-drawn without rebuilding them.</summary>
    private readonly List<Swatch> _swatches = new();

    public GeneralPage(ISettingsService settings, ThemeService theme, StartupService startup)
    {
        _settings = settings;
        _theme = theme;
        _startup = startup;
        InitializeComponent();
        DataContext = _settings.Current;
        BuildPalette();
        Load();
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;
            SelectCombo(ThemeCombo, s.Theme);
            SelectCombo(WidgetSizeCombo, s.DictationWidgetSize);
            StartupToggle.IsChecked = s.LaunchOnStartup;
            TrayToggle.IsChecked = s.MinimizeToTray;
            BackdropToggle.IsChecked = s.EnableBackdrop;
            HighlightPalette(s.AccentColor);
        }
        finally { _loading = false; }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var value = (ThemeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "Dark";
        _settings.Update(s => s.Theme = value);

        // Re-apply everything, not just the theme: WPF-UI resets the accent to the Windows
        // colorization color whenever a theme is applied, which would drop the user's color.
        ApplyLook();
        HighlightPalette(_settings.Current.AccentColor);
    }

    /// <summary>
    /// Switches the floating dictation widget between its full and compact layouts. The widget
    /// itself listens for the saved change and re-lays out immediately, so there is nothing to
    /// apply here beyond persisting it.
    /// </summary>
    private void WidgetSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var value = WidgetSizes.Normalize((WidgetSizeCombo.SelectedItem as ComboBoxItem)?.Content as string);
        _settings.Update(s => s.DictationWidgetSize = value);
    }

    // --- Color palette ---

    /// <summary>One clickable palette entry: a colored tile inside a selection ring.</summary>
    private sealed record Swatch(string Hex, Border Ring, SymbolIcon Check);

    /// <summary>Builds the clickable color swatches.</summary>
    private void BuildPalette()
    {
        ColorPanel.Children.Clear();
        _swatches.Clear();

        foreach (var hex in Palette)
        {
            var color = ParseColor(hex);

            // A checkmark drawn in whichever of black/white contrasts with the tile, so the
            // selection is legible on gold and on indigo alike.
            var check = new SymbolIcon
            {
                Symbol = SymbolRegular.Checkmark24,
                FontSize = 15,
                Foreground = new SolidColorBrush(
                    ThemeService.IsLightColor(color) ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White),
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var tile = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(color),
                SnapsToDevicePixels = true,
                Child = check,
                // A soft colored glow makes the palette read as light rather than as stickers.
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = color,
                    BlurRadius = 14,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                },
            };

            // The ring lives on an outer border so it reads against the card background
            // rather than fighting the tile color — a plain white ring was invisible in the
            // light theme, leaving no way to tell which color was active.
            var ring = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                ToolTip = hex,
                Tag = hex,
                Child = tile,
            };

            // Tunneling mouse-down, marked handled. The card this sits in no longer takes mouse
            // capture (SettingCard is a plain ContentControl, unlike the ButtonBase-derived
            // CardControl it replaced), but handling the gesture at the swatch is still the
            // right place for it — nothing above should see the click at all.
            ring.PreviewMouseLeftButtonDown += Swatch_Click;

            ColorPanel.Children.Add(ring);
            _swatches.Add(new Swatch(hex, ring, check));
        }
    }

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border ring || ring.Tag is not string hex) return;

        // Keep the enclosing card from taking mouse capture (and from registering a click).
        e.Handled = true;

        _settings.Update(s => s.AccentColor = hex);
        HighlightPalette(hex);
        ApplyLook();
    }

    /// <summary>Rings + check-marks the active swatch so the current color is obvious.</summary>
    private void HighlightPalette(string hex)
    {
        foreach (var swatch in _swatches)
        {
            var selected = swatch.Hex.Equals(hex, StringComparison.OrdinalIgnoreCase);
            if (selected)
            {
                // A theme resource reference (not a fixed brush) so the ring stays visible
                // after the user switches between light and dark.
                swatch.Ring.SetResourceReference(Border.BorderBrushProperty, "Wp.TextPrimary");
            }
            else
            {
                swatch.Ring.BorderBrush = Brushes.Transparent;
            }
            swatch.Check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Pushes theme + color + backdrop from settings onto the running app.</summary>
    private void ApplyLook()
    {
        var s = _settings.Current;
        _theme.ApplyAll(s.Theme, s.AccentColor, s.EnableBackdrop);
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = StartupToggle.IsChecked == true;
        _settings.Update(s => s.LaunchOnStartup = enabled);
        _startup.SetEnabled(enabled);
    }

    private void TrayToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s.MinimizeToTray = TrayToggle.IsChecked == true);
    }

    private void BackdropToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s.EnableBackdrop = BackdropToggle.IsChecked == true);

        // The toggle used to only persist the value, so the glass effect never actually
        // turned off until the app was restarted (and not even then, since the window asked
        // for Mica in XAML).
        ApplyLook();
    }

    private static void SelectCombo(ItemsControl combo, string value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (item.Content as string) == value)
            {
                if (combo is System.Windows.Controls.Primitives.Selector s) s.SelectedIndex = i;
                return;
            }
        }
        if (combo is System.Windows.Controls.Primitives.Selector sel) sel.SelectedIndex = 0;
    }
}
