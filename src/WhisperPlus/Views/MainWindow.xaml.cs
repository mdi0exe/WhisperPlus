using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Serilog;
using Wpf.Ui.Controls;
using WhisperPlus.Services;
using WhisperPlus.Views.Controls;

namespace WhisperPlus.Views;

/// <summary>
/// Main application window: a navigation rail beside a content panel that hosts the settings
/// pages. Pages are created on first navigation via a DI-aware cache
/// (<see cref="DiPageProvider"/>) so each page receives the services it needs through its
/// constructor, and keeps its state when the user navigates away and back. Minimizes to the
/// tray on close (per user setting).
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly ISettingsService _settings;
    private readonly TrayService _tray;

    /// <summary>
    /// Shared with the rail: the same provider the old NavigationView was given, so page
    /// instances are still resolved from the container and cached per type.
    /// </summary>
    private readonly DiPageProvider _pages = new(App.Services);

    public MainWindow(ISettingsService settings, TrayService tray)
    {
        _settings = settings;
        _tray = tray;
        InitializeComponent();

        // One handler for the whole rail. NavItem is a RadioButton, so the group already
        // guarantees exactly one is checked; all this has to do is show the matching page.
        foreach (var item in new[] { NavGeneral, NavHotkeys, NavModels, NavLanguages, NavAudio, NavAbout })
            item.Checked += NavItem_Checked;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The XAML asks for Mica; honour the user's Glass-backdrop setting instead.
        if (!_settings.Current.EnableBackdrop) WindowBackdropType = WindowBackdropType.None;

        NavGeneral.IsChecked = true; // raises Checked → navigates to the first page
    }

    /// <summary>
    /// Keeps the content panel clipped to its own rounded corners. A Border does not clip its
    /// child to its <c>CornerRadius</c>, so without this a scrolled page (or the corner bloom)
    /// paints straight over the rounded edge.
    /// </summary>
    private void OnContentPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ContentPanel.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 17, 17);
    }

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not NavItem { PageType: { } pageType }) return;
        if (_pages.GetPage(pageType) is not UIElement page) return;

        // Pages are cached, so one that reads disk state (installed models, detector backends) would
        // otherwise keep showing whatever was true when it was first built — a model downloaded on
        // another page never appeared until the app was restarted.
        if (page is IRefreshablePage refreshable)
        {
            try { refreshable.Refresh(); }
            catch (Exception ex) { Log.Warning(ex, "Could not refresh {Page}.", pageType.Name); }
        }

        PageHost.Content = page;
        AnimatePageIn(page);

        // Pages are cached, and a page can also arrive already scrolled (setting a selection
        // during construction pulls that control into view). Either way, arriving halfway down
        // a settings page reads as a glitch — so every navigation starts at the top.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FindScrollViewer(page)?.ScrollToHome());
    }

    /// <summary>Depth-first search for the page's outermost <see cref="ScrollViewer"/>.</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// Fades and lifts the incoming page a few pixels. Short enough that it never delays a
    /// click, long enough that the eye follows the change instead of being startled by it.
    /// </summary>
    private static void AnimatePageIn(UIElement page)
    {
        var slide = new TranslateTransform();
        page.RenderTransform = slide;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (ApplicationIsExiting) return;

        if (_settings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Tray apps use ShutdownMode.OnExplicitShutdown, so closing the last window no longer
        // ends the process on its own. With "minimize to tray" off, closing means quit.
        ApplicationIsExiting = true;
        Application.Current.Shutdown();
    }

    /// <summary>Flag set by App during a real shutdown so OnClosing doesn't re-hide.</summary>
    public static bool ApplicationIsExiting { get; set; }

    public void BringToForeground()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }
}
