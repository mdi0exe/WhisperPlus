using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WhisperPlus.Services;

/// <summary>
/// Centralizes application of the visual theme (dark/light/system), the single user-chosen
/// color (used for both the accent and the background wash), and the Mica/Acrylic backdrop.
/// <see cref="ApplyAll"/> is the only entry point: the steps are order-dependent, so every
/// settings change funnels through it and the whole look is re-applied as one unit.
/// </summary>
public sealed class ThemeService
{
    /// <summary>
    /// The fixed tint strength (as a percent, 0–100 → alpha 0–255). The strength slider was
    /// removed from the UI; the tint is always applied at this subtle, constant wash. Color
    /// is still user-selectable via the palette.
    /// </summary>
    public const int TintOpacityPercent = 5;

    /// <summary>Fallback color used when the configured <c>AccentColor</c> is missing or
    /// unparseable, so neither the accent nor the tint ever silently freezes on bad input.</summary>
    private static readonly Color DefaultColor =
        (Color)ColorConverter.ConvertFromString("#6759FF");

    /// <summary>
    /// Applies theme, color (accent + background wash), and backdrop to the running app.
    /// </summary>
    /// <remarks>
    /// The step order is the whole trick, and it is not obvious:
    /// <list type="number">
    /// <item>WPF-UI's control templates capture their accent brushes when the template is
    /// applied, and the brushes in its theme dictionary are frozen. So writing new accent
    /// resources — via <c>ApplicationAccentColorManager</c> or by hand — updates the
    /// dictionary but leaves every already-rendered button and toggle at its old color.
    /// Re-applying the *theme* is what makes the templates re-resolve, and that is the only
    /// thing that actually recolors a live window.</item>
    /// <item>The theme apply must therefore come last — and must pass
    /// <c>updateAccent: false</c>, because WPF-UI otherwise resets the accent to the Windows
    /// colorization color and the user's pick would be thrown away.</item>
    /// </list>
    /// </remarks>
    public void ApplyAll(string themeName, string colorHex, bool enableBackdrop = true)
    {
        var theme = ResolveTheme(themeName);
        var color = TryParseColor(colorHex, out var parsed) ? parsed : DefaultColor;

        PublishAccentResources(color, theme);
        PublishAuroraResources(color, theme);

        // Re-apply the theme so the templates pick the new accent up. updateAccent: false
        // keeps WPF-UI from overwriting what we just published.
        ApplicationThemeManager.Apply(theme, BackdropFor(enableBackdrop), updateAccent: false);

        ApplyWindowBackdrops(enableBackdrop);
        ApplyBackground(color);
    }

    /// <summary>
    /// Returns the resolved <see cref="ApplicationTheme"/> for a settings theme name,
    /// without applying anything. Anything other than "light" resolves to Dark — including
    /// a legacy "System" value from an older install, which no longer has a UI option.
    /// </summary>
    public ApplicationTheme ResolveTheme(string themeName) =>
        themeName?.Trim().Equals("light", StringComparison.OrdinalIgnoreCase) == true
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

    /// <summary>
    /// Writes the user's color and every accent-derived brush WPF-UI's templates consume into
    /// <see cref="Application.Resources"/>. Entries in the top-level dictionary take
    /// precedence over the merged theme dictionary, so these survive the theme re-apply that
    /// follows — and that re-apply is what makes them visible.
    /// </summary>
    private static void PublishAccentResources(Color accent, ApplicationTheme theme)
    {
        // Let WPF-UI derive its own ramp first (it also drives the title-bar accent), then
        // override the pieces we want to control precisely.
        try { ApplicationAccentColorManager.Apply(accent, theme); }
        catch { /* never let a theming hiccup take the app down */ }

        var dark = theme == ApplicationTheme.Dark;

        // Hover/pressed fills step away from the base color: lighter on dark backgrounds,
        // darker on light ones — the same direction WinUI's accent ramp moves.
        var primary = Shift(accent, dark ? 12 : -6);
        var secondary = Shift(accent, dark ? 24 : -12);
        var tertiary = Shift(accent, dark ? 36 : -18);

        // Accent-colored *text* sits directly on the page background, so it has to travel
        // further than the fills do or light accents (gold, coral) become unreadable.
        var accentText = Shift(accent, dark ? 45 : -30);

        // Glyphs and labels drawn on top of an accent fill: pick whichever of black/white
        // actually contrasts with the chosen color.
        var onAccent = IsLightColor(accent) ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White;

        var r = Application.Current.Resources;

        r["SystemAccentColor"] = accent;
        r["SystemAccentColorPrimary"] = primary;
        r["SystemAccentColorSecondary"] = secondary;
        r["SystemAccentColorTertiary"] = tertiary;

        SetBrush(r, "SystemAccentColorBrush", accent);
        SetBrush(r, "SystemAccentBrush", accent);
        SetBrush(r, "SystemAccentColorPrimaryBrush", primary);
        SetBrush(r, "SystemAccentColorSecondaryBrush", secondary);
        SetBrush(r, "SystemAccentColorTertiaryBrush", tertiary);

        // Fills: buttons with Appearance="Primary", toggle switches, selection indicators.
        SetBrush(r, "AccentFillColorDefaultBrush", accent);
        SetBrush(r, "AccentFillColorSecondaryBrush", primary);
        SetBrush(r, "AccentFillColorTertiaryBrush", secondary);
        SetBrush(r, "AccentFillColorSelectedTextBackgroundBrush", accent);
        SetBrush(r, "AccentControlElevationBorderBrush", tertiary);
        SetBrush(r, "AccentAcrylicFillColorDefaultBrush", Color.FromArgb(0x66, accent.R, accent.G, accent.B));

        // Accent-colored text (hyperlinks, badges, focus text).
        SetBrush(r, "AccentTextFillColorPrimaryBrush", accentText);
        SetBrush(r, "AccentTextFillColorSecondaryBrush", accentText);
        SetBrush(r, "AccentTextFillColorTertiaryBrush", accent);

        // Text/glyphs painted on top of an accent fill.
        SetBrush(r, "TextOnAccentFillColorPrimaryBrush", onAccent);
        SetBrush(r, "TextOnAccentFillColorSecondaryBrush", Color.FromArgb(0xC8, onAccent.R, onAccent.G, onAccent.B));
        SetBrush(r, "TextOnAccentFillColorSelectedTextBrush", onAccent);
    }

    /// <summary>
    /// Publishes the app's own design tokens — the <c>Wp.*</c> surfaces, strokes, text ramps and
    /// accent gradients that every custom template in <c>Themes/</c> consumes.
    /// </summary>
    /// <remarks>
    /// <para>These exist because the redesigned UI needs two things WPF-UI's dictionary does not
    /// give it: translucent layered surfaces that read correctly on top of Mica, and brushes
    /// derived from the *user's* color rather than a fixed accent. Everything is republished on
    /// each <see cref="ApplyAll"/> and consumed with <c>DynamicResource</c>, so switching theme
    /// or color restyles the running app without a restart.</para>
    /// <para>Fresh brush instances are created every time rather than mutated — brushes that
    /// land in <see cref="Application.Resources"/> are frozen and would throw on mutation (the
    /// same reason the tint brush is re-assigned rather than edited).</para>
    /// </remarks>
    private static void PublishAuroraResources(Color accent, ApplicationTheme theme)
    {
        var r = Application.Current.Resources;
        var dark = theme == ApplicationTheme.Dark;

        // --- Layered surfaces. On dark these are white washes so the Mica backdrop shows
        // through; on light they are white veils over the same backdrop. ---
        SetBrush(r, "Wp.Surface", dark ? Argb(0x0E, 0xFF, 0xFF, 0xFF) : Argb(0xAE, 0xFF, 0xFF, 0xFF));
        SetBrush(r, "Wp.SurfaceHover", dark ? Argb(0x1A, 0xFF, 0xFF, 0xFF) : Argb(0xE0, 0xFF, 0xFF, 0xFF));
        SetBrush(r, "Wp.SurfaceStrong", dark ? Argb(0x1C, 0xFF, 0xFF, 0xFF) : Argb(0xC4, 0xFF, 0xFF, 0xFF));
        SetBrush(r, "Wp.SurfaceSunken", dark ? Argb(0x38, 0x00, 0x00, 0x00) : Argb(0x0C, 0x00, 0x00, 0x00));
        SetBrush(r, "Wp.SurfaceFlyout", dark ? Argb(0xF2, 0x1B, 0x1D, 0x26) : Argb(0xF7, 0xFB, 0xFB, 0xFD));

        // --- Hairlines. Bright edge on dark, soft shadow edge on light. ---
        SetBrush(r, "Wp.Stroke", dark ? Argb(0x1F, 0xFF, 0xFF, 0xFF) : Argb(0x16, 0x00, 0x00, 0x00));
        SetBrush(r, "Wp.StrokeStrong", dark ? Argb(0x38, 0xFF, 0xFF, 0xFF) : Argb(0x28, 0x00, 0x00, 0x00));

        // --- Text ramp. ---
        SetBrush(r, "Wp.TextPrimary", dark ? Argb(0xF5, 0xFF, 0xFF, 0xFF) : Argb(0xE8, 0x0B, 0x0C, 0x11));
        SetBrush(r, "Wp.TextSecondary", dark ? Argb(0xC2, 0xFF, 0xFF, 0xFF) : Argb(0xB0, 0x0B, 0x0C, 0x11));
        SetBrush(r, "Wp.TextTertiary", dark ? Argb(0x8C, 0xFF, 0xFF, 0xFF) : Argb(0x8A, 0x0B, 0x0C, 0x11));

        // --- Accent family, all derived from the one color the user picked. ---
        var accentHover = Shift(accent, dark ? 14 : -8);
        var accentLift = Shift(accent, dark ? 34 : 16);
        var accentText = Shift(accent, dark ? 45 : -30);
        var onAccent = IsLightColor(accent) ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White;

        SetBrush(r, "Wp.Accent", accent);
        SetBrush(r, "Wp.AccentHover", accentHover);
        SetBrush(r, "Wp.AccentText", accentText);
        SetBrush(r, "Wp.AccentOn", onAccent);
        SetBrush(r, "Wp.AccentFaint", Argb(0x1C, accent.R, accent.G, accent.B));
        SetBrush(r, "Wp.AccentSoft", Argb(0x33, accent.R, accent.G, accent.B));
        SetBrush(r, "Wp.AccentGlow", Argb(0x70, accent.R, accent.G, accent.B));
        SetBrush(r, "Wp.AccentEdge", Argb(dark ? (byte)0x66 : (byte)0x4D, accent.R, accent.G, accent.B));

        // Diagonal accent gradient used on primary buttons, icon tiles and the brand mark. The
        // lift keeps the top-left brighter, which is what makes a flat fill read as a surface.
        r["Wp.AccentGradient"] = Linear(accentLift, accent, 0.15, 0, 0.85, 1);
        r["Wp.AccentGradientVertical"] = Linear(accentLift, accent, 0.5, 0, 0.5, 1);

        // Very low-alpha wash for hero panels — visible as a tint, never as a color block.
        r["Wp.AccentWash"] = Linear(
            Argb(dark ? (byte)0x30 : (byte)0x24, accent.R, accent.G, accent.B),
            Argb(0x00, accent.R, accent.G, accent.B),
            0, 0, 1, 1);

        // The waveform's bars: bright at the crest, accent at the base.
        r["Wp.WaveGradient"] = Linear(Shift(accent, 55), accent, 0.5, 0, 0.5, 1);

        // Sheen drawn across the top edge of raised surfaces, so cards catch "light".
        r["Wp.Sheen"] = Linear(
            Argb(dark ? (byte)0x18 : (byte)0x60, 0xFF, 0xFF, 0xFF),
            Argb(0x00, 0xFF, 0xFF, 0xFF),
            0.5, 0, 0.5, 1);

        // Corner bloom behind the content panel. Weak enough to read as light rather than as a
        // colored shape, which is what keeps a large flat surface from looking like cardboard.
        var bloom = new RadialGradientBrush
        {
            Center = new Point(0.85, 0.08),
            GradientOrigin = new Point(0.85, 0.08),
            RadiusX = 0.85,
            RadiusY = 1.0,
        };
        bloom.GradientStops.Add(new GradientStop(Argb(dark ? (byte)0x26 : (byte)0x1E, accent.R, accent.G, accent.B), 0));
        bloom.GradientStops.Add(new GradientStop(Argb(0x00, accent.R, accent.G, accent.B), 1));
        r["Wp.AccentBloom"] = bloom;
    }

    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    /// <summary>Builds a two-stop linear gradient between two points in relative coordinates.</summary>
    private static LinearGradientBrush Linear(Color from, Color to, double x1, double y1, double x2, double y2) =>
        new(from, to, new Point(x1, y1), new Point(x2, y2));

    /// <summary>
    /// Applies the background tint wash behind the window, using the same color the user
    /// picked for the accent at the fixed <see cref="TintOpacityPercent"/> strength.
    /// </summary>
    /// <remarks>
    /// Each call publishes a fresh brush under the <c>WhisperPlusTintBrush</c> key rather
    /// than mutating the previous one. Brushes that live in
    /// <see cref="Application.Resources"/> are frozen by WPF for thread-safety and become
    /// read-only — mutating their <see cref="SolidColorBrush.Color"/> throws
    /// <c>InvalidOperationException</c>. Re-assigning the key still updates the overlay
    /// live, because the overlay binds it with <c>DynamicResource</c> directly (not through
    /// a control template), so it re-resolves whenever the entry changes.
    /// </remarks>
    private static void ApplyBackground(Color color)
    {
        var alpha = (byte)Math.Clamp((int)(TintOpacityPercent / 100.0 * 255), 0, 255);
        SetBrush(Application.Current.Resources, "WhisperPlusTintBrush",
            Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    /// <summary>
    /// Pushes the Mica/Acrylic setting onto every open window. The backdrop type is baked
    /// into each window when it loads, so flipping the setting has to reach the existing
    /// windows and not just the application-level theme.
    /// </summary>
    private static void ApplyWindowBackdrops(bool enabled)
    {
        var backdrop = BackdropFor(enabled);
        foreach (var window in Application.Current.Windows.OfType<FluentWindow>())
        {
            try { window.WindowBackdropType = backdrop; }
            catch { /* unsupported OS build — the DWM calls no-op */ }
        }
    }

    private static WindowBackdropType BackdropFor(bool enabled) =>
        enabled ? WindowBackdropType.Mica : WindowBackdropType.None;

    private static void SetBrush(ResourceDictionary resources, string key, Color color) =>
        resources[key] = new SolidColorBrush(color);

    /// <summary>
    /// Moves a color towards white (positive <paramref name="percent"/>) or black
    /// (negative) by that fraction. A straight channel lerp keeps the hue intact, which is
    /// what the accent ramp needs — an HSL brightness change would drift saturated colors.
    /// </summary>
    private static Color Shift(Color color, double percent)
    {
        var t = Math.Clamp(Math.Abs(percent) / 100.0, 0, 1);
        var target = percent >= 0 ? 255.0 : 0.0;
        return Color.FromRgb(
            (byte)Math.Round(color.R + (target - color.R) * t),
            (byte)Math.Round(color.G + (target - color.G) * t),
            (byte)Math.Round(color.B + (target - color.B) * t));
    }

    /// <summary>True when a color is bright enough that white text on it would be unreadable.</summary>
    public static bool IsLightColor(Color color)
    {
        // Rec. 601 luma — cheap and good enough for a black/white text decision.
        var luma = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luma > 0.6;
    }

    /// <summary>Tries to parse a 6/8-digit hex color (e.g. "#6759FF"); returns false on
    /// any malformed input instead of throwing.</summary>
    private static bool TryParseColor(string? hex, out Color color)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                color = (Color)ColorConverter.ConvertFromString(hex);
                return true;
            }
            catch { /* fall through to failure */ }
        }
        color = default;
        return false;
    }
}
