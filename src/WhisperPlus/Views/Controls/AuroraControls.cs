using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace WhisperPlus.Views.Controls;

/// <summary>
/// The large heading that opens every settings page: an accent-gradient icon tile, the page
/// title, a one-line subtitle, and an optional badge pill on the right.
/// </summary>
/// <remarks>
/// These are lookless controls — the templates live in <c>Themes/Components.xaml</c>, which is
/// merged into <see cref="Application.Resources"/>. WPF resolves an implicit
/// <c>TargetType</c> style from the application dictionary, so no <c>Themes/Generic.xaml</c>
/// (and no <c>ThemeInfo</c> assembly attribute) is needed.
/// </remarks>
public class PageHeader : Control
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader),
            new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader),
            new PropertyMetadata(string.Empty));

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(PageHeader),
            new PropertyMetadata(SymbolRegular.Empty));

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Optional pill shown at the trailing edge (empty string hides it).</summary>
    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.Register(nameof(Badge), typeof(string), typeof(PageHeader),
            new PropertyMetadata(string.Empty));

    public string Badge
    {
        get => (string)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    /// <summary>Glyph shown inside the badge pill.</summary>
    public static readonly DependencyProperty BadgeIconProperty =
        DependencyProperty.Register(nameof(BadgeIcon), typeof(SymbolRegular), typeof(PageHeader),
            new PropertyMetadata(SymbolRegular.Sparkle24));

    public SymbolRegular BadgeIcon
    {
        get => (SymbolRegular)GetValue(BadgeIconProperty);
        set => SetValue(BadgeIconProperty, value);
    }
}

/// <summary>
/// A group divider inside a page: a short accent rule, an all-caps label, and an optional
/// explanatory line underneath.
/// </summary>
public class SectionHeader : Control
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SectionHeader),
            new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(SectionHeader),
            new PropertyMetadata(string.Empty));

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }
}

/// <summary>
/// The workhorse settings row: an icon tile, a title, a wrapped description, and the control
/// itself (toggle, combo box, buttons…) as <see cref="ContentControl.Content"/> on the right.
/// </summary>
/// <remarks>
/// Deliberately a <see cref="ContentControl"/> rather than a button-like card: the old
/// WPF-UI <c>CardControl</c> derives from <c>ButtonBase</c> and took mouse capture on
/// mouse-down, which is why the color swatches and language chips had to intercept the
/// tunneling event to get their clicks at all. Nothing here captures the mouse.
/// </remarks>
public class SettingCard : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingCard),
            new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard),
            new PropertyMetadata(string.Empty));

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(SettingCard),
            new PropertyMetadata(SymbolRegular.Empty));

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>
    /// Lays the content out under the header instead of beside it — for rows whose control is
    /// too wide to sit in a trailing column (palettes, chip grids).
    /// </summary>
    public static readonly DependencyProperty IsStackedProperty =
        DependencyProperty.Register(nameof(IsStacked), typeof(bool), typeof(SettingCard),
            new PropertyMetadata(false));

    public bool IsStacked
    {
        get => (bool)GetValue(IsStackedProperty);
        set => SetValue(IsStackedProperty, value);
    }

    /// <summary>Draws the card with an accent wash — used to mark the active item in a list.</summary>
    public static readonly DependencyProperty IsHighlightedProperty =
        DependencyProperty.Register(nameof(IsHighlighted), typeof(bool), typeof(SettingCard),
            new PropertyMetadata(false));

    public bool IsHighlighted
    {
        get => (bool)GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }
}

/// <summary>
/// One entry in the main window's navigation rail. A <see cref="RadioButton"/> so the group
/// handles mutual exclusion for us; <see cref="PageType"/> is the page the shell navigates to
/// when it becomes checked.
/// </summary>
public class NavItem : RadioButton
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(NavItem),
            new PropertyMetadata(string.Empty));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(NavItem),
            new PropertyMetadata(SymbolRegular.Empty));

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The <see cref="UserControl"/> type hosted when this item is selected.</summary>
    public static readonly DependencyProperty PageTypeProperty =
        DependencyProperty.Register(nameof(PageType), typeof(Type), typeof(NavItem),
            new PropertyMetadata(null));

    public Type? PageType
    {
        get => (Type?)GetValue(PageTypeProperty);
        set => SetValue(PageTypeProperty, value);
    }
}

/// <summary>
/// A small rounded status pill (“ACTIVE”, “Private and offline”, a shortcut gesture…). The
/// <see cref="Tone"/> picks the color family; the template does the rest.
/// </summary>
public class Badge : ContentControl
{
    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(nameof(Tone), typeof(BadgeTone), typeof(Badge),
            new PropertyMetadata(BadgeTone.Accent));

    public BadgeTone Tone
    {
        get => (BadgeTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(Badge),
            new PropertyMetadata(SymbolRegular.Empty));

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}

/// <summary>Color families available to <see cref="Badge"/>.</summary>
public enum BadgeTone
{
    Accent,
    Success,
    Warning,
    Danger,
    Neutral,
}

/// <summary>
/// Wraps a piece of content in the app's glass card treatment (rounded, hairline stroke,
/// translucent fill) without the header/description layout of <see cref="SettingCard"/>.
/// </summary>
public class GlassPanel : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(GlassPanel),
            new PropertyMetadata(new CornerRadius(16)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>Paints the panel with the accent wash + accent hairline instead of the neutral one.</summary>
    public static readonly DependencyProperty IsAccentProperty =
        DependencyProperty.Register(nameof(IsAccent), typeof(bool), typeof(GlassPanel),
            new PropertyMetadata(false));

    public bool IsAccent
    {
        get => (bool)GetValue(IsAccentProperty);
        set => SetValue(IsAccentProperty, value);
    }
}
