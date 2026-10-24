using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;

namespace WhisperPlus.ScreenCapture;

/// <summary>
/// A snip kept on screen: borderless, always-on-top, draggable, resizable with zoom, and independent
/// of the overlay that produced it.
/// </summary>
public partial class PinnedWindow : Window
{
    private const double ShadowMargin = 18.0;
    private const double CornerRadiusPx = 10.0;

    private readonly BitmapSource _image;
    private readonly double _baseWidth;
    private readonly double _baseHeight;
    private double _zoom = 1.0;
    private double _pinOpacity = 1.0;
    private DispatcherTimer? _badgeTimer;

    private static readonly List<PinnedWindow> Open = new();

    public event Action? PinClosed;

    public PinnedWindow(BitmapSource image, Rect screenPlacementDip)
    {
        InitializeComponent();

        _image = image;
        Shot.Source = image;

        _baseWidth = Math.Max(1, screenPlacementDip.Width);
        _baseHeight = Math.Max(1, screenPlacementDip.Height);

        Shot.Width = _baseWidth;
        Shot.Height = _baseHeight;

        // Offset window by ShadowMargin so the rendered image content aligns perfectly
        // with the original desktop selection.
        Left = screenPlacementDip.Left - ShadowMargin;
        Top = screenPlacementDip.Top - ShadowMargin;

        MouseEnter += (_, _) => SetHoverState(true);
        MouseLeave += (_, _) => SetHoverState(false);
        Closed += (_, _) =>
        {
            _badgeTimer?.Stop();
            lock (Open) Open.Remove(this);
            PinClosed?.Invoke();
        };

        lock (Open) Open.Add(this);
    }

    public static void CloseAll()
    {
        PinnedWindow[] all;
        lock (Open) all = Open.ToArray();
        foreach (var pin in all)
        {
            try { pin.Close(); }
            catch (Exception ex) { Log.Debug(ex, "Could not close a pinned window."); }
        }
    }

    /// <summary>
    /// Creates a pinned window from an image currently on the Windows clipboard, or returns null
    /// if the clipboard does not contain an image.
    /// </summary>
    public static PinnedWindow? FromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsImage()) return null;
            var image = Clipboard.GetImage();
            if (image is null) return null;

            var workArea = SystemParameters.WorkArea;
            double dipWidth = image.Width;
            double dipHeight = image.Height;

            // Constrain initial size to fit comfortably on screen
            double maxW = workArea.Width * 0.85;
            double maxH = workArea.Height * 0.85;

            if (dipWidth > maxW || dipHeight > maxH)
            {
                double scale = Math.Min(maxW / dipWidth, maxH / dipHeight);
                dipWidth *= scale;
                dipHeight *= scale;
            }

            double left = workArea.Left + (workArea.Width - dipWidth) / 2;
            double top = workArea.Top + (workArea.Height - dipHeight) / 2;

            return new PinnedWindow(image, new Rect(left, top, dipWidth, dipHeight));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create PinnedWindow from clipboard.");
            return null;
        }
    }

    private void OnImageClipHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ImageClipHost.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
            CornerRadiusPx, CornerRadiusPx);
    }

    private void SetHoverState(bool hovered)
    {
        HudBar.IsHitTestVisible = hovered;
        var duration = TimeSpan.FromMilliseconds(140);
        HudBar.BeginAnimation(OpacityProperty, new DoubleAnimation(hovered ? 1.0 : 0.0, duration));

        if (hovered)
            FrameBorder.SetResourceReference(Border.BorderBrushProperty, "Wp.Accent");
        else
            FrameBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    }

    private void ApplyZoom(double newZoom, Point? anchorInImage = null)
    {
        newZoom = Math.Clamp(newZoom, 0.2, 4.0);
        if (Math.Abs(newZoom - _zoom) < 0.001) return;

        double oldW = Shot.Width;
        double oldH = Shot.Height;
        double newW = Math.Round(_baseWidth * newZoom);
        double newH = Math.Round(_baseHeight * newZoom);

        double rx = anchorInImage.HasValue && oldW > 0 ? anchorInImage.Value.X / oldW : 0.5;
        double ry = anchorInImage.HasValue && oldH > 0 ? anchorInImage.Value.Y / oldH : 0.5;

        Left -= (newW - oldW) * rx;
        Top -= (newH - oldH) * ry;

        _zoom = newZoom;
        Shot.Width = newW;
        Shot.Height = newH;

        RenderOptions.SetBitmapScalingMode(Shot,
            Math.Abs(_zoom - 1.0) < 0.01 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        ShowBadge($"{(int)Math.Round(_zoom * 100)}%");
    }

    private void ApplyPinOpacity(double opacity)
    {
        _pinOpacity = Math.Clamp(opacity, 0.2, 1.0);
        ImageClipHost.Opacity = _pinOpacity;
        ShowBadge($"Opacity {(int)Math.Round(_pinOpacity * 100)}%");
    }

    private void ShowBadge(string text)
    {
        StatusBadgeText.Text = text;
        StatusBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(80)));

        _badgeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(950) };
        _badgeTimer.Stop();
        _badgeTimer.Tick -= HideBadge;
        _badgeTimer.Tick += HideBadge;
        _badgeTimer.Start();
    }

    private void HideBadge(object? sender, EventArgs e)
    {
        _badgeTimer?.Stop();
        StatusBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(220)));
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null)
            return;

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null)
            return;

        ApplyZoom(1.0);
        e.Handled = true;
    }

    private void OnZoomWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ApplyPinOpacity(_pinOpacity + (e.Delta > 0 ? 0.08 : -0.08));
        }
        else
        {
            var pos = e.GetPosition(Shot);
            ApplyZoom(_zoom * (e.Delta > 0 ? 1.12 : 1.0 / 1.12), pos);
        }
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            CopyImage();
            e.Handled = true;
        }
    }

    private void OnFrameRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.ContextMenu is { } menu)
        {
            menu.PlacementTarget = fe;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void CopyImage()
    {
        try
        {
            Clipboard.SetImage(_image);
            ShowBadge("Copied");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not copy the pinned image to the clipboard.");
        }
    }

    private async void OnOcr(object sender, RoutedEventArgs e)
    {
        if (!OcrService.IsAvailable)
        {
            ShowBadge("OCR unavailable");
            return;
        }

        ShowBadge("Reading text…");
        var result = await OcrService.RecognizeAsync(_image);
        if (result is null || !result.HasText)
        {
            ShowBadge("No text found");
            return;
        }

        try
        {
            Clipboard.SetText(result.Text);
            ShowBadge($"Copied {result.Text.Length} chars");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not copy OCR text to clipboard.");
            ShowBadge("Clipboard locked");
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e) => CopyImage();
    private void OnActualSize(object sender, RoutedEventArgs e) => ApplyZoom(1.0);
    private void OnResetOpacity(object sender, RoutedEventArgs e) => ApplyPinOpacity(1.0);
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
