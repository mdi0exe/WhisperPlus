using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using SkiaSharp;
using WhisperPlus.ScreenCapture.Editor;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfSize = System.Windows.Size;

namespace WhisperPlus.ScreenCapture;

/// <summary>How a capture session starts: a dragged region, or the whole virtual desktop.</summary>
public enum CaptureMode
{
    Region,
    FullScreen,
}

/// <summary>
/// A single full-screen overlay that both selects a region and annotates it, Lightshot style:
/// there is no second editor window, so the screenshot is taken once and everything happens
/// over a frozen copy of the desktop.
///
/// <para><b>Lifetime and memory.</b> One instance per capture, created on demand and closed as
/// soon as the image is copied or saved. <see cref="Dispose"/> releases the captured GDI bitmap,
/// the Skia scene (which owns a second full-resolution bitmap) and the annotation canvas, so the
/// process returns to its idle footprint instead of holding a screen-sized buffer per session.</para>
/// </summary>
public partial class CaptureOverlayWindow : Window
{
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);

    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private enum Phase { Select, Edit }

    private static readonly string[] Palette =
    {
        "#FFE5484D", // red
        "#FFF2D600", // yellow
        "#FF2FBF71", // green
        "#FF2F6FED", // blue
        "#FF000000", // black
        "#FFFFFFFF", // white
    };

    private static readonly string[] Fonts = { "Segoe UI", "Arial", "Calibri", "Consolas", "Georgia", "Impact", "Verdana" };

    // Chrome brushes come from the app's floating-widget palette (OverlayTheme.xaml), so the
    // overlay looks like the dictation/recorder pills instead of a separate product.
    private Brush ChromeText => (Brush)FindResource("OverlayTextBrush");
    private Brush ChromeTextDim => (Brush)FindResource("OverlayTextDimBrush");
    private Brush ChromeStroke => (Brush)FindResource("OverlayChromeStrokeBrush");
    private Brush AccentBrush => (Brush)FindResource("Wp.Accent");

    private static readonly (EditorTool Tool, string Icon, string Name, string Key)[] Tools =
    {
        (EditorTool.Select,    "IconCursor",      "Select / move", "V"),
        (EditorTool.Rectangle, "IconSquare",      "Rectangle",     "R"),
        (EditorTool.Ellipse,   "IconCircle",      "Ellipse",       "O"),
        (EditorTool.Arrow,     "IconArrow",       "Arrow",         "A"),
        (EditorTool.Line,      "IconLine",        "Line",          "L"),
        (EditorTool.Pen,       "IconPen",         "Pen",           "P"),
        (EditorTool.Highlight, "IconHighlighter", "Highlighter",   "H"),
        (EditorTool.Text,      "IconText",        "Text",          "T"),
        (EditorTool.Step,      "IconStep",        "Step number",   "S"),
        (EditorTool.Blur,      "IconBlur",        "Blur",          "B"),
    };

    private readonly DrawingBitmap _screenshot;
    private readonly DrawingRectangle _virtualBounds;
    private readonly ToolStyleMemory _style;
    private readonly Dictionary<EditorTool, ToggleButton> _toolButtons = new();

    private CaptureMode _mode;
    private Phase _phase = Phase.Select;
    private bool _dragging;
    private bool _pendingNewSelection;
    private bool _newSelectionArmed;
    private WpfPoint _start;
    private WpfRect _selDip;
    private DrawingRectangle _pixelRegion;
    private Scene? _scene;
    private InteractiveCanvas? _canvas;
    private double _pixelScaleX = 1, _pixelScaleY = 1;
    private int _lastSizeW = int.MinValue, _lastSizeH = int.MinValue;
    private bool _disposed;
    private HwndSource? _source;

    // Inline text editing state.
    private TextBox? _textBox;
    private TextItem? _editingItem;
    private string _editBeforeText = "";
    private bool _editIsNewItem;
    private bool _editCommitted;
    private bool _textEditing;

    private DispatcherTimer? _toastTimer;

    public CaptureOverlayWindow(DrawingBitmap screenshot, DrawingRectangle virtualBounds,
                                ToolStyleMemory style, CaptureMode mode)
    {
        _screenshot = screenshot;
        _virtualBounds = virtualBounds;
        _style = style;
        _mode = mode;

        InitializeComponent();
        ScreenImage.Source = ScreenCaptureCore.ToBitmapSource(screenshot);

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        PreviewKeyDown += OnPreviewKeyDown;
        KeyDown += OnKeyDown;
    }

    /// <summary>Folder the Save dialog starts in.</summary>
    public string SaveDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    /// <summary>"png", "jpg" or "webp" — pre-selects the Save dialog filter.</summary>
    public string OutputFormat { get; set; } = "png";

    /// <summary>Encoder quality for JPEG/WebP (1–100).</summary>
    public int Quality { get; set; } = 92;

    /// <summary>Raised when the user picks a directory in the Save dialog, so settings can persist it.</summary>
    public event Action<string>? SaveDirectoryChanged;

    /// <summary>Raised once the window has closed, so the owner can drop its reference.</summary>
    public event Action? SessionEnded;

    // ===================== Window plumbing =====================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Kept so the hook can be removed on close: an HwndSource hook is a native callback into
        // this window's method, and leaving it installed pins the window after it is closed.
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(OverlayInputHook);
        // One HWND for the whole virtual desktop: every monitor is covered by this window,
        // which is also why no click can reach the desktop underneath while it is open.
        MoveWindow(hwnd, _virtualBounds.X, _virtualBounds.Y, _virtualBounds.Width, _virtualBounds.Height, true);
    }

    /// <summary>
    /// Without this, the first click on an overlay that is not yet the foreground window is
    /// swallowed by Windows as an activation click: the crosshair appears, the drag never starts.
    /// </summary>
    private IntPtr OverlayInputHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_ACTIVATE = 1;
        if (msg != WM_MOUSEACTIVATE) return IntPtr.Zero;
        handled = true;
        return new IntPtr(MA_ACTIVATE);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ScreenImage.Width = ActualWidth;
        ScreenImage.Height = ActualHeight;
        CachePixelScale();
        UpdateDimRects(WpfRect.Empty);
        BuildModeBar();
        ApplyMode();
        EnsureForeground();
    }

    private void EnsureForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        IntPtr fore = GetForegroundWindow();
        uint foreThread = fore == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fore, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = false;
        if (foreThread != 0 && foreThread != thisThread)
            attached = AttachThreadInput(foreThread, thisThread, true);
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            Activate();
            Focus();
        }
        finally
        {
            if (attached) AttachThreadInput(foreThread, thisThread, false);
        }
    }

    // ===================== Mode bar =====================

    private void BuildModeBar()
    {
        ModeStack.Children.Clear();
        AddModeButton("IconRegion", "Region", CaptureMode.Region);
        AddModeButton("IconFullscreen", "Full screen", CaptureMode.FullScreen);
    }

    private void AddModeButton(string icon, string label, CaptureMode mode)
    {
        var btn = new ToggleButton
        {
            IsChecked = _mode == mode,
            Cursor = Cursors.Hand,
            Height = 34,
            Padding = new Thickness(10, 0, 12, 0),
            Margin = new Thickness(1),
            Foreground = ChromeText,
            Background = Brushes.Transparent,
            ToolTip = label,
            Style = TryFindResource("ModeChip") as Style,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(icon),
            Stroke = ChromeText,
            StrokeThickness = 1.8,
            Width = 17,
            Height = 17,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Foreground = ChromeText,
        });
        btn.Content = sp;
        btn.Click += (_, _) =>
        {
            if (_mode == mode) return;
            _mode = mode;
            ApplyMode();
        };
        ModeStack.Children.Add(btn);
    }

    private void ApplyMode()
    {
        int i = 0;
        foreach (ToggleButton b in ModeStack.Children.OfType<ToggleButton>())
            b.IsChecked = (CaptureMode)i++ == _mode;

        // Switching modes mid-session restarts from a clean surface: the annotations belong to
        // the old region and cannot be carried over to a different canvas size.
        if (_phase == Phase.Edit) LeaveEditPhase();

        ResetSelection();
        if (_mode == CaptureMode.FullScreen)
        {
            HintText.Text = "Full screen — annotate, then copy or save";
            _selDip = new WpfRect(0, 0, ActualWidth, ActualHeight);
            _pixelRegion = new DrawingRectangle(0, 0, _screenshot.Width, _screenshot.Height);
            EnterEditPhase();
        }
        else
        {
            HintText.Text = "Drag to select an area  ·  Esc to cancel";
            Cursor = Cursors.Cross;
            ModeBar.Visibility = Visibility.Visible;
            HintBox.Visibility = Visibility.Visible;
            Toolbar.Visibility = Visibility.Collapsed;
            ExportBox.Visibility = Visibility.Collapsed;
            OptionBar.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(new Action(PositionTopBars), DispatcherPriority.Loaded);
        }
    }

    private void PositionTopBars()
    {
        var mon = GetUiMonitorDip();
        ModeBar.UpdateLayout();
        PlaceTopCenter(ModeBar, mon, 18);
        HintBox.UpdateLayout();
        double hintTop = Canvas.GetTop(ModeBar) + ModeBar.ActualHeight + 10;
        PlaceTopCenter(HintBox, mon, hintTop - mon.Top);
    }

    // ===================== Selection phase =====================

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var pos = e.GetPosition(Root);

        if (_phase == Phase.Select)
        {
            _dragging = true;
            _start = pos;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_pickingColor)
        {
            int px = Math.Clamp((int)Math.Round(pos.X * _pixelScaleX), 0, _screenshot.Width - 1);
            int py = Math.Clamp((int)Math.Round(pos.Y * _pixelScaleY), 0, _screenshot.Height - 1);
            var color = _screenshot.GetPixel(px, py);
            _pickingColor = false;
            if (_canvas is not null) _canvas.PickingColor = false;
            Cursor = Cursors.Arrow;

            var display = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            _style.StrokeColor = display;
            try { Clipboard.SetText(display); }
            catch { }

            ShowToast($"Copied {display}");
            BuildExportBar();
            BuildOptionBar();
            e.Handled = true;
            return;
        }

        // Edit phase: only a drag that starts outside the captured region begins a new one.
        if (!_selDip.Contains(pos))
        {
            _pendingNewSelection = true;
            _newSelectionArmed = false;
            _dragging = true;
            _start = pos;
            CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Root);

        if (!_dragging)
        {
            if (_phase == Phase.Select) Cursor = Cursors.Cross;
            return;
        }

        if (_pendingNewSelection && !_newSelectionArmed)
        {
            if (Math.Abs(pos.X - _start.X) < 6 && Math.Abs(pos.Y - _start.Y) < 6) return;
            _newSelectionArmed = true;
            LeaveEditPhase();
            Toolbar.Visibility = Visibility.Collapsed;
            ExportBox.Visibility = Visibility.Collapsed;
            OptionBar.Visibility = Visibility.Collapsed;
            SelectionBorder.Visibility = Visibility.Collapsed;
            UpdateDimRects(WpfRect.Empty);
        }

        HideSelectChrome();
        UpdateSelectionVisual(MakeRect(_start, pos));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        if (_pendingNewSelection)
        {
            _pendingNewSelection = false;
            bool armed = _newSelectionArmed;
            _newSelectionArmed = false;
            if (!armed) { RestoreEditChrome(); return; }
            var rect = MakeRect(_start, e.GetPosition(Root));
            if (!IsUsableSelection(rect)) { RestoreEditChrome(); return; }
            _selDip = rect;
            _pixelRegion = ToPixelRegion(rect);
            EnterEditPhase();
            return;
        }

        if (_phase != Phase.Select) return;

        var sel = MakeRect(_start, e.GetPosition(Root));
        if (!IsUsableSelection(sel))
        {
            ResetSelection();
            ModeBar.Visibility = Visibility.Visible;
            HintBox.Visibility = Visibility.Visible;
            return;
        }
        _selDip = sel;
        _pixelRegion = ToPixelRegion(sel);
        EnterEditPhase();
    }

    private void RestoreEditChrome()
    {
        if (_phase != Phase.Edit) return;
        SelectionBorder.Visibility = Visibility.Visible;
        UpdateDimRects(_selDip);
        Toolbar.Visibility = Visibility.Visible;
        ExportBox.Visibility = Visibility.Visible;
        BuildOptionBar();
        PositionPanels();
    }

    private static WpfRect MakeRect(WpfPoint a, WpfPoint b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new WpfRect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>
    /// A selection is usable when it has a little area. Either extent being long is enough, so
    /// thin strips (a status bar, a single line of text) are still capturable.
    /// </summary>
    private static bool IsUsableSelection(WpfRect rect)
    {
        const double minDim = 4, minExtent = 20;
        if (rect.Width < minDim || rect.Height < minDim) return false;
        return rect.Width >= minExtent || rect.Height >= minExtent;
    }

    private void HideSelectChrome()
    {
        ModeBar.Visibility = Visibility.Collapsed;
        HintBox.Visibility = Visibility.Collapsed;
    }

    private void UpdateDimRects(WpfRect hole)
    {
        double w = ActualWidth, h = ActualHeight;
        if (hole.IsEmpty || hole.Width <= 0 || hole.Height <= 0)
        {
            DimTop.Width = w; DimTop.Height = h;
            Canvas.SetLeft(DimTop, 0); Canvas.SetTop(DimTop, 0);
            DimBottom.Height = 0; DimLeft.Width = 0; DimRight.Width = 0;
            return;
        }
        DimTop.Width = w; DimTop.Height = Math.Max(0, hole.Y);
        Canvas.SetLeft(DimTop, 0); Canvas.SetTop(DimTop, 0);

        double botY = hole.Y + hole.Height;
        DimBottom.Width = w; DimBottom.Height = Math.Max(0, h - botY);
        Canvas.SetLeft(DimBottom, 0); Canvas.SetTop(DimBottom, botY);

        DimLeft.Width = Math.Max(0, hole.X); DimLeft.Height = hole.Height;
        Canvas.SetLeft(DimLeft, 0); Canvas.SetTop(DimLeft, hole.Y);

        double rightX = hole.X + hole.Width;
        DimRight.Width = Math.Max(0, w - rightX); DimRight.Height = hole.Height;
        Canvas.SetLeft(DimRight, rightX); Canvas.SetTop(DimRight, hole.Y);
    }

    private void UpdateSelectionVisual(WpfRect rect)
    {
        UpdateDimRects(rect);
        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Opacity = 1.0;
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        Canvas.SetLeft(SelectionBorder, rect.X);
        Canvas.SetTop(SelectionBorder, rect.Y);

        var px = ToPixelRegion(rect);
        if (px.Width != _lastSizeW || px.Height != _lastSizeH)
        {
            _lastSizeW = px.Width;
            _lastSizeH = px.Height;
            SizeText.Text = $"{px.Width} × {px.Height}";
        }
        SizeBadge.Visibility = Visibility.Visible;
        double by = rect.Y - 26 < 0 ? rect.Y + 6 : rect.Y - 26;
        Canvas.SetLeft(SizeBadge, rect.X);
        Canvas.SetTop(SizeBadge, by);
    }

    private void ResetSelection()
    {
        UpdateDimRects(WpfRect.Empty);
        SelectionBorder.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
    }

    private void CachePixelScale()
    {
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            _pixelScaleX = _screenshot.Width / ActualWidth;
            _pixelScaleY = _screenshot.Height / ActualHeight;
        }
    }

    private DrawingRectangle ToPixelRegion(WpfRect rect)
    {
        double sx = _pixelScaleX, sy = _pixelScaleY;
        if (sx <= 0 || sy <= 0)
        {
            CachePixelScale();
            sx = _pixelScaleX; sy = _pixelScaleY;
        }
        var r = new DrawingRectangle(
            (int)Math.Round(rect.X * sx), (int)Math.Round(rect.Y * sy),
            (int)Math.Round(rect.Width * sx), (int)Math.Round(rect.Height * sy));
        r.Intersect(new DrawingRectangle(0, 0, _screenshot.Width, _screenshot.Height));
        return r;
    }

    // ===================== Edit phase =====================

    private void EnterEditPhase()
    {
        _phase = Phase.Edit;
        Cursor = Cursors.Arrow;
        HintBox.Visibility = Visibility.Collapsed;
        ModeBar.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Opacity = 0.35;
        SelectionBorder.Width = _selDip.Width;
        SelectionBorder.Height = _selDip.Height;
        Canvas.SetLeft(SelectionBorder, _selDip.X);
        Canvas.SetTop(SelectionBorder, _selDip.Y);
        UpdateDimRects(_selDip);

        DetachScene();

        using (var cropped = ScreenCaptureCore.Crop(_screenshot, _pixelRegion))
            _scene = new Scene { Background = ToSkBitmap(cropped) };
        _scene.Changed += OnSceneChanged;

        _canvas = new InteractiveCanvas(_scene, _style) { Layout = LayoutMode.OneToOne };
        _canvas.TextEditRequested += OnTextEditRequested;
        _canvas.CropRequested += img => _canvas?.BeginCrop(img);
        _canvas.SelectionChanged += BuildOptionBar;
        _canvas.ColorPickRequested += OnColorPicked;
        _canvas.ToolChanged += () =>
        {
            if (_pickingColor)
            {
                _pickingColor = false;
                if (_canvas is not null) _canvas.PickingColor = false;
                Cursor = Cursors.Arrow;
                BuildExportBar();
            }
            SyncToolButtons();
            BuildOptionBar();
        };
        _canvas.ItemMoved += () => Dispatcher.BeginInvoke(new Action(BuildOptionBar), DispatcherPriority.Render);

        EditHost.Child = _canvas;
        EditHost.Visibility = Visibility.Visible;
        Canvas.SetLeft(EditHost, _selDip.X);
        Canvas.SetTop(EditHost, _selDip.Y);
        EditHost.Width = _selDip.Width;
        EditHost.Height = _selDip.Height;

        ToolStack.Orientation = _mode == CaptureMode.FullScreen ? Orientation.Horizontal : Orientation.Vertical;
        BuildToolbar();
        BuildExportBar();
        BuildOptionBar();
        PositionPanels();
        _canvas.Focus();
    }

    private void LeaveEditPhase()
    {
        DetachScene();
        _phase = Phase.Select;
        Cursor = Cursors.Cross;
        EditHost.Child = null;
        EditHost.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Collapsed;
        ExportBox.Visibility = Visibility.Collapsed;
        OptionBar.Visibility = Visibility.Collapsed;
        SelectionBorder.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        _canvas = null;
        _scene = null;
        ResetSelection();
    }

    private void DetachScene()
    {
        if (_scene == null) return;
        _scene.Changed -= OnSceneChanged;
        _canvas?.Detach();
        _canvas = null;
        // The scene owns a full-resolution copy of the capture plus every annotation's Skia caches
        // (baked blurs, pasted bitmaps, freehand paths) and the snapshots on its undo stacks.
        // ReleaseResources walks all of them; disposing only the background left the rest of that
        // native memory allocated for the life of the process.
        _scene.ReleaseResources();
        _scene = null;
    }

    private void OnSceneChanged()
    {
        if (OptionBar.Visibility == Visibility.Visible)
            PositionOptionBar();
    }

    private static SKBitmap ToSkBitmap(DrawingBitmap bmp)
    {
        var data = bmp.LockBits(new DrawingRectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var sk = new SKBitmap(info);
            unsafe { Buffer.MemoryCopy((void*)data.Scan0, (void*)sk.GetPixels(), sk.ByteCount, data.Stride * bmp.Height); }
            return sk;
        }
        finally { bmp.UnlockBits(data); }
    }

    // ===================== Tool rail =====================

    private void BuildToolbar()
    {
        ToolStack.Children.Clear();
        _toolButtons.Clear();
        foreach (var (tool, icon, name, key) in Tools)
        {
            var btn = new ToggleButton
            {
                Style = (Style)FindResource("IconToolButton"),
                Tag = FindResource(icon),
                IsChecked = tool == (_canvas?.Tool ?? EditorTool.Select),
                ToolTip = $"{name}  [{key}]",
            };
            btn.Click += (_, _) => { if (_canvas != null) _canvas.Tool = tool; };
            _toolButtons[tool] = btn;
            ToolStack.Children.Add(btn);
        }
        Toolbar.Visibility = Visibility.Visible;
    }

    private void SyncToolButtons()
    {
        foreach (var (tool, btn) in _toolButtons)
            btn.IsChecked = tool == _canvas?.Tool;
    }

    // ===================== Export bar =====================

    private void BuildExportBar()
    {
        ExportStack.Children.Clear();
        ExportStack.Children.Add(MakeCmd("IconReset", "Undo", "Undo (Ctrl+Z)", () => _scene?.Undo()));
        ExportStack.Children.Add(MakeCmd("IconFit", "Redo", "Redo (Ctrl+Y)", () => _scene?.Redo()));
        ExportStack.Children.Add(MakeSep());
        ExportStack.Children.Add(MakeCmd("IconCopy", "Copy", "Copy to clipboard (Ctrl+C)", CopyAndClose, accent: true));
        ExportStack.Children.Add(MakeCmd("IconOcr", "OCR", "Copy the text in the selection (Windows OCR, offline)", RunOcr));
        ExportStack.Children.Add(MakeCmd("IconEyedropper", "Colour", "Pick a colour from the capture and copy its hex", ToggleColorPicker, accent: _pickingColor));
        ExportStack.Children.Add(MakeCmd("IconPin", "Pin", "Keep this snip on screen, always on top", PinToScreen));
        ExportStack.Children.Add(MakeCmd("IconSave", "Save", "Save to file (Ctrl+S)", Save));
        ExportStack.Children.Add(MakeCmd("IconClose", "Close", "Discard and close (Esc)", Close));
        ExportBox.Visibility = Visibility.Visible;
    }

    private Button MakeCmd(string icon, string label, string tip, Action onClick, bool accent = false)
    {
        var b = new Button
        {
            Cursor = Cursors.Hand,
            Height = 34,
            Padding = new Thickness(10, 0, 12, 0),
            Margin = new Thickness(1),
            Foreground = ChromeText,
            Background = accent ? AccentBrush : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Style = TryFindResource("ActionChip") as Style,
            ToolTip = tip,
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(icon),
            Stroke = ChromeText,
            StrokeThickness = 1.8,
            Width = 17,
            Height = 17,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Foreground = ChromeText,
        });
        b.Content = sp;
        b.Click += (_, _) => onClick();
        return b;
    }

    private Border MakeSep() => new()
    {
        Width = 1,
        Height = 18,
        Margin = new Thickness(4, 0, 4, 0),
        Background = ChromeStroke,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ===================== Option bar =====================

    /// <summary>
    /// Snapshot of the selection taken when a style drag (slider) starts, so the whole drag
    /// collapses into one undo step instead of one per pixel of mouse movement.
    /// </summary>
    private List<SceneItem>? _styleTargets;
    private List<SceneItem>? _styleBefore;

    /// <summary>
    /// Runs a style edit on the current selection and records it in the undo history.
    ///
    /// <para>Every option-bar control used to mutate the item and call <c>RaiseChanged</c>. That
    /// repainted correctly but pushed nothing onto the history, so Ctrl+Z skipped straight past the
    /// colour or font change and undid whatever came before it (usually creating or moving the
    /// item) — the style change itself could never be undone.</para>
    /// </summary>
    private void EditSelection(Action apply)
    {
        var targets = _canvas?.Selection;
        if (_scene is null || targets is null || targets.Count == 0)
        {
            apply();
            _scene?.RaiseChanged();
            return;
        }

        var before = new List<SceneItem>(targets.Count);
        foreach (var item in targets) before.Add(item.Clone());
        apply();
        CommitStyleEdit(targets, before);
    }

    /// <summary>Starts a coalesced style edit (a slider drag). Safe to call repeatedly.</summary>
    private void BeginStyleEdit()
    {
        if (_styleTargets is not null) return;
        var targets = _canvas?.Selection;
        if (_scene is null || targets is null || targets.Count == 0) return;

        _styleTargets = new List<SceneItem>(targets);
        _styleBefore = new List<SceneItem>(targets.Count);
        foreach (var item in targets) _styleBefore.Add(item.Clone());
    }

    /// <summary>Closes a coalesced style edit with a single history entry.</summary>
    private void CommitStyleEdit()
    {
        var targets = _styleTargets;
        var before = _styleBefore;
        _styleTargets = null;
        _styleBefore = null;
        if (targets is null || before is null) return;
        CommitStyleEdit(targets, before);
    }

    private void CommitStyleEdit(List<SceneItem> targets, List<SceneItem> before)
    {
        if (_scene is null) return;

        var actions = new List<IUndoableAction>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
        {
            if (HasItemChanged(targets[i], before[i]))
                actions.Add(new ModifyItemAction(targets[i], before[i], targets[i].Clone()));
        }

        if (actions.Count == 0) return;
        _scene.Apply(actions.Count == 1 ? actions[0] : new CompositeAction(actions));
    }

    private static bool HasItemChanged(SceneItem a, SceneItem b)
    {
        if (a.Bounds != b.Bounds || a.Rotation != b.Rotation ||
            a.StrokeColor != b.StrokeColor || a.FillColor != b.FillColor ||
            Math.Abs(a.StrokeWidth - b.StrokeWidth) > 1e-4f ||
            Math.Abs(a.Opacity - b.Opacity) > 1e-4f)
            return true;

        if (a is ArrowItem aArr && b is ArrowItem bArr)
            return Math.Abs(aArr.HeadScale - bArr.HeadScale) > 1e-4f || aArr.BendPoint != bArr.BendPoint;

        if (a is LineItem aLine && b is LineItem bLine)
            return aLine.Start != bLine.Start || aLine.End != bLine.End;

        if (a is RectItem aRect && b is RectItem bRect)
            return Math.Abs(aRect.CornerRadius - bRect.CornerRadius) > 1e-4f;

        if (a is TextItem aTxt && b is TextItem bTxt)
            return aTxt.Text != bTxt.Text || aTxt.FontFamily != bTxt.FontFamily ||
                   Math.Abs(aTxt.FontSize - bTxt.FontSize) > 1e-4f ||
                   aTxt.Bold != bTxt.Bold || aTxt.Italic != bTxt.Italic ||
                   aTxt.Shadow != bTxt.Shadow || aTxt.Ribbon != bTxt.Ribbon;

        if (a is StepItem aStep && b is StepItem bStep)
            return aStep.Shape != bStep.Shape || aStep.BadgeColor != bStep.BadgeColor ||
                   aStep.NumberColor != bStep.NumberColor || Math.Abs(aStep.Diameter - bStep.Diameter) > 1e-4f;

        if (a is BlurItem aBlur && b is BlurItem bBlur)
            return Math.Abs(aBlur.Strength - bBlur.Strength) > 1e-4f || aBlur.Pixelate != bBlur.Pixelate;

        if (a is ImageItem aImg && b is ImageItem bImg)
            return aImg.CropRect != bImg.CropRect;

        return false;
    }

    private void BuildOptionBar()
    {
        OptionStack.Children.Clear();
        if (_canvas == null)
        {
            OptionBar.Visibility = Visibility.Collapsed;
            return;
        }

        var sel = _canvas.SelectedItem;
        var all = _canvas.Selection;
        var tool = _canvas.Tool;
        bool hasSelection = sel != null;

        if (!hasSelection && tool == EditorTool.Select)
        {
            OptionBar.Visibility = Visibility.Collapsed;
            return;
        }

        bool multi = all.Count > 1;
        bool allText = multi && all.All(s => s is TextItem);
        bool allStep = multi && all.All(s => s is StepItem);
        bool allBlur = multi && all.All(s => s is BlurItem);
        bool allArrow = multi && all.All(s => s is ArrowItem);

        bool colorable = hasSelection
            ? all.Any(s => s is not BlurItem and not ImageItem)
            : tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow
                or EditorTool.Pen or EditorTool.Highlight or EditorTool.Text or EditorTool.Step;

        if (colorable && (!hasSelection || tool != EditorTool.Blur))
        {
            AddColorRow(c =>
            {
                string hex = InteractiveCanvas.HexFromColor(c);
                EditSelection(() =>
                {
                    if (!multi)
                    {
                        if (tool == EditorTool.Step || sel is StepItem)
                        {
                            if (sel is StepItem si) si.BadgeColor = c;
                            _style.StepColor = hex;
                        }
                        else
                        {
                            if (sel != null) sel.StrokeColor = c;
                            if (tool == EditorTool.Text || sel is TextItem) _style.TextColor = hex;
                            else _style.StrokeColor = hex;
                        }
                    }
                    else
                    {
                        foreach (var s in all)
                        {
                            if (s is StepItem si) si.BadgeColor = c;
                            else if (s is not BlurItem and not ImageItem) s.StrokeColor = c;
                        }
                        _style.StrokeColor = hex;
                    }
                });
            });
        }

        // Fill for rectangles and ellipses.
        bool fillRelevant = !multi
            ? tool is EditorTool.Rectangle or EditorTool.Ellipse || sel is RectItem or EllipseItem
            : all.Any(s => s is RectItem or EllipseItem);
        if (fillRelevant)
        {
            AddSep();
            bool filled = !multi
                ? sel?.FillColor.Alpha > 0
                : all.Where(s => s is RectItem or EllipseItem).Any(s => s.FillColor.Alpha > 0);
            AddChip("Fill", filled, on =>
            {
                EditSelection(() =>
                {
                    foreach (var s in all)
                    {
                        if (s is RectItem or EllipseItem)
                            s.FillColor = on ? s.StrokeColor.WithAlpha(70) : SKColors.Transparent;
                    }
                    _style.FillColor = on
                        ? InteractiveCanvas.HexFromColor(InteractiveCanvas.ColorFromHex(_style.StrokeColor).WithAlpha(70))
                        : "#00000000";
                });
            });
        }

        // Stroke width.
        bool strokeRelevant = !multi
            ? tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow
                or EditorTool.Pen or EditorTool.Highlight
                || sel is RectItem or EllipseItem or LineItem or FreehandItem
            : all.Any(s => s is RectItem or EllipseItem or LineItem or FreehandItem);
        if (strokeRelevant)
        {
            AddSep();
            AddSlider(null, sel?.StrokeWidth ?? _style.StrokeWidth, 1, 24, v =>
            {
                foreach (var s in all)
                    if (s is RectItem or EllipseItem or LineItem or FreehandItem) s.StrokeWidth = (float)v;
                _style.StrokeWidth = v;
            });
        }

        // Arrow head size.
        if (!multi ? tool == EditorTool.Arrow || sel is ArrowItem : allArrow)
        {
            AddSep();
            AddSlider(null, (sel as ArrowItem)?.HeadScale ?? _style.ArrowHeadScale, 0.5, 3, v =>
            {
                foreach (var s in all)
                    if (s is ArrowItem a) a.HeadScale = (float)v;
                _style.ArrowHeadScale = v;
            });
        }

        // Text options.
        if (!multi ? tool == EditorTool.Text || sel is TextItem : allText)
        {
            AddSep();
            AddChip("B", (sel as TextItem)?.Bold ?? _style.FontBold, on =>
            {
                EditSelection(() =>
                {
                    foreach (var s in all) if (s is TextItem ti) { ti.Bold = on; ti.Measure(); }
                    _style.FontBold = on;
                });
            }, bold: true);
            AddChip("I", (sel as TextItem)?.Italic ?? _style.FontItalic, on =>
            {
                EditSelection(() =>
                {
                    foreach (var s in all) if (s is TextItem ti) { ti.Italic = on; ti.Measure(); }
                    _style.FontItalic = on;
                });
            }, italic: true);
            AddSep();
            AddSlider(null, (sel as TextItem)?.FontSize ?? _style.FontSize, 10, 96, v =>
            {
                foreach (var s in all) if (s is TextItem ti) { ti.FontSize = (float)v; ti.Measure(); }
                _style.FontSize = v;
            });
            AddSep();
            AddFontCycleButton();
            AddSep();
            AddChip("Shadow", (sel as TextItem)?.Shadow ?? _style.TextShadow, on =>
            {
                EditSelection(() =>
                {
                    foreach (var s in all) if (s is TextItem ti) ti.Shadow = on;
                    _style.TextShadow = on;
                });
            });
        }

        // Step marker options.
        if (!multi ? tool == EditorTool.Step || sel is StepItem : allStep)
        {
            AddSep();
            var current = (sel as StepItem)?.Shape ?? _style.StepShape;
            var shapeButtons = new List<ToggleButton>();
            foreach (var (icon, tip, shape) in new[]
                     {
                         ("IconStepCircle", "Circle", StepShape.Circle),
                         ("IconStepSquare", "Square", StepShape.Square),
                         ("IconStepBubble", "Bubble", StepShape.Bubble),
                     })
            {
                var s = shape;
                var btn = MakeIconChipButton(icon, tip, current == s);
                btn.Click += (_, _) =>
                {
                    EditSelection(() =>
                    {
                        foreach (var it in all) if (it is StepItem si) si.Shape = s;
                        _style.StepShape = s;
                    });
                    foreach (var ob in shapeButtons) ob.IsChecked = false;
                    btn.IsChecked = true;
                };
                shapeButtons.Add(btn);
                OptionStack.Children.Add(btn);
            }
            AddSep();
            var reset = MakeIconChipButton("IconReset", "Restart numbering at 1", false);
            reset.Click += (_, _) =>
            {
                _scene?.ResetStepCounter();
                reset.IsChecked = false;
                ShowToast("Step counter reset");
            };
            OptionStack.Children.Add(reset);
        }

        // Blur options.
        if (!multi ? tool == EditorTool.Blur || sel is BlurItem : allBlur)
        {
            AddSep();
            AddSlider(null, (sel as BlurItem)?.Strength ?? _style.BlurStrength, 2, 24, v =>
            {
                foreach (var s in all) if (s is BlurItem bi) { bi.Strength = (float)v; bi.NeedsBake = true; }
                _style.BlurStrength = v;
            });
            AddChip("Pixelate", (sel as BlurItem)?.Pixelate ?? _style.BlurPixelate, on =>
            {
                EditSelection(() =>
                {
                    foreach (var s in all) if (s is BlurItem bi) { bi.Pixelate = on; bi.NeedsBake = true; }
                    _style.BlurPixelate = on;
                });
            });
        }

        while (OptionStack.Children.Count > 0 && OptionStack.Children[0] is Border { Width: 1 })
            OptionStack.Children.RemoveAt(0);
        while (OptionStack.Children.Count > 0 && OptionStack.Children[^1] is Border { Width: 1 })
            OptionStack.Children.RemoveAt(OptionStack.Children.Count - 1);

        OptionBar.Visibility = OptionStack.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(new Action(PositionPanels), DispatcherPriority.Loaded);
    }

    private void AddColorRow(Action<SKColor> onPick)
    {
        var currentHex = _style.StrokeColor.ToUpperInvariant();
        var paletteList = new List<string>(Palette);
        if (!string.IsNullOrEmpty(currentHex) && !paletteList.Any(p => string.Equals(p, currentHex, StringComparison.OrdinalIgnoreCase)))
        {
            paletteList.Insert(0, currentHex);
        }

        foreach (var hex in paletteList)
        {
            var c = InteractiveCanvas.ColorFromHex(hex);
            bool isCurrent = string.Equals(hex, currentHex, StringComparison.OrdinalIgnoreCase);
            var sw = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(1.5, 0, 1.5, 0),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue)),
                BorderBrush = isCurrent ? Brushes.White : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x5E, 0x70)),
                BorderThickness = new Thickness(isCurrent ? 2 : 1),
                Cursor = Cursors.Hand,
                ToolTip = isCurrent ? $"{hex} (Current)" : hex,
                VerticalAlignment = VerticalAlignment.Center,
            };
            sw.MouseLeftButtonDown += (_, e) => { onPick(c); e.Handled = true; };
            OptionStack.Children.Add(sw);
        }
    }

    private void AddSlider(string? label, double value, double min, double max, Action<double> onChange)
    {
        if (label != null)
        {
            OptionStack.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = ChromeTextDim,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 4, 0),
            });
        }

        var s = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = 56,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 1, 0),
        };
        if (TryFindResource("ToolSlider") is Style sliderStyle) s.Style = sliderStyle;

        var valLabel = new TextBlock
        {
            Text = ((int)Math.Round(s.Value)).ToString(),
            Foreground = ChromeTextDim,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 16,
            Margin = new Thickness(0, 0, 1, 0),
        };
        // A drag is one undo step: snapshot the selection on the first tick, publish the change
        // live (so the shape follows the thumb), and record a single history entry on release.
        // Recording per tick would fill the undo stack with dozens of one-pixel steps.
        s.ValueChanged += (_, e) =>
        {
            BeginStyleEdit();
            onChange(e.NewValue);
            _scene?.RaiseChanged();
            valLabel.Text = ((int)Math.Round(e.NewValue)).ToString();
            if (!s.IsMouseCaptureWithin) CommitStyleEdit();
        };
        s.PreviewMouseLeftButtonDown += (_, _) => BeginStyleEdit();
        s.PreviewMouseLeftButtonUp += (_, _) => CommitStyleEdit();
        s.LostMouseCapture += (_, _) => CommitStyleEdit();
        OptionStack.Children.Add(s);
        OptionStack.Children.Add(valLabel);
    }

    private void AddChip(string label, bool active, Action<bool> onToggle, bool bold = false, bool italic = false)
    {
        var b = new ToggleButton
        {
            Content = label,
            IsChecked = active,
            Cursor = Cursors.Hand,
            MinWidth = 24,
            Height = 24,
            Margin = new Thickness(1, 0, 1, 0),
            Padding = new Thickness(5, 0, 5, 0),
            Foreground = ChromeText,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryFindResource("ModeChip") as Style,
            ToolTip = label,
        };
        if (bold) b.FontWeight = FontWeights.Bold;
        if (italic) b.FontStyle = FontStyles.Italic;
        b.Click += (_, _) => onToggle(b.IsChecked == true);
        OptionStack.Children.Add(b);
    }

    /// <summary>
    /// Cycles the annotation font. A combo box would need a full dark template; a cycling chip
    /// keeps the option bar a fixed width and is enough for the handful of fonts offered.
    /// </summary>
    private void AddFontCycleButton()
    {
        var current = (_canvas?.SelectedItem as TextItem)?.FontFamily ?? _style.FontFamily;
        var b = new Button
        {
            Content = new TextBlock { Text = current, FontSize = 11, Foreground = ChromeText },
            Cursor = Cursors.Hand,
            Height = 24,
            Padding = new Thickness(7, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryFindResource("ActionChip") as Style,
            ToolTip = "Click to cycle the annotation font",
        };
        b.Click += (_, _) =>
        {
            int i = Array.IndexOf(Fonts, current);
            string next = Fonts[(i + 1) % Fonts.Length];
            EditSelection(() =>
            {
                // Every selected text, not just the first one: Bold and Italic already applied to
                // the whole selection, so changing the font on a multi-selection used to touch one
                // item and leave the rest in the old face.
                foreach (var item in _canvas?.Selection ?? new List<SceneItem>())
                {
                    if (item is not TextItem ti) continue;
                    ti.FontFamily = next;
                    // Bounds follows the font; without re-measuring, the selection box and the
                    // hit area kept the metrics of the previous face.
                    ti.Measure();
                }
                _style.FontFamily = next;
            });
            current = next;
            ((TextBlock)b.Content).Text = next;
        };
        OptionStack.Children.Add(b);
    }

    private ToggleButton MakeIconChipButton(string iconKey, string tooltip, bool active)
    {
        var geo = TryFindResource(iconKey) as Geometry;
        var path = new System.Windows.Shapes.Path
        {
            Data = geo,
            Stroke = ChromeText,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent,
            Width = 24,
            Height = 24,
            Stretch = Stretch.Uniform,
        };
        var tb = new ToggleButton
        {
            Content = new Viewbox { Width = 14, Height = 14, Child = path },
            IsChecked = active,
            Cursor = Cursors.Hand,
            Width = 26,
            Height = 26,
            Margin = new Thickness(1, 0, 1, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryFindResource("ModeChip") as Style,
            ToolTip = tooltip,
        };
        return tb;
    }

    private void AddSep() => OptionStack.Children.Add(MakeSep());

    // ===================== Panel placement =====================

    private WpfRect GetUiMonitorDip()
    {
        // Follow the mouse: on a multi-monitor desk the overlay spans every screen, but the
        // chrome belongs on the one the user is working on.
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        GetCursorPos(out POINT cursor);
        var mon = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref info))
            return new WpfRect(0, 0, ActualWidth, ActualHeight);

        return new WpfRect(
            (info.rcMonitor.Left - _virtualBounds.X) / sx,
            (info.rcMonitor.Top - _virtualBounds.Y) / sy,
            (info.rcMonitor.Right - info.rcMonitor.Left) / sx,
            (info.rcMonitor.Bottom - info.rcMonitor.Top) / sy);
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);

    /// <summary>
    /// Layout scale for the chrome, from the monitor the panels will sit on.
    ///
    /// <para>The overlay is a single HWND covering the whole virtual desktop, so WPF gives it one
    /// DPI. On a secondary monitor with a different scale the toolbar would then render at the wrong
    /// physical size; <see cref="ChromeScale"/> converts the monitor's real DPI into a layout scale,
    /// which is 1 on every single-monitor setup and on matching-DPI setups.</para>
    /// </summary>
    private double ChromeScaleAt(double dipX, double dipY)
    {
        try
        {
            var screen = PointToScreen(new WpfPoint(dipX, dipY));
            return ChromeScale.ForScreenPoint(this, (int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        }
        catch
        {
            return 1;
        }
    }

    private void PositionPanels()
    {
        if (_phase != Phase.Edit) return;
        var mon = GetUiMonitorDip();

        // Scale the chrome for the monitor it will land on, then measure the scaled bounds.
        var anchor = _mode == CaptureMode.FullScreen
            ? new WpfPoint(mon.Left + mon.Width / 2, mon.Top + mon.Height / 2)
            : new WpfPoint(_selDip.X + _selDip.Width / 2, _selDip.Y + _selDip.Height / 2);
        var chromeScale = ChromeScaleAt(anchor.X, anchor.Y);
        ChromeScale.Apply(Toolbar, chromeScale);
        ChromeScale.Apply(ExportBox, chromeScale);
        ChromeScale.Apply(OptionBar, chromeScale);

        Toolbar.UpdateLayout();
        var tbSize = ChromeScale.LayoutSize(Toolbar);
        double tbW = tbSize.Width, tbH = tbSize.Height;
        const double gap = 10;

        if (_mode == CaptureMode.FullScreen)
        {
            double tbX = Math.Round(mon.Left + (mon.Width - tbW) / 2);
            double tbY = mon.Bottom - tbH - 24;
            Canvas.SetLeft(Toolbar, Math.Clamp(tbX, mon.Left + gap, Math.Max(mon.Left + gap, mon.Right - tbW - gap)));
            Canvas.SetTop(Toolbar, Math.Clamp(tbY, mon.Top + gap, Math.Max(mon.Top + gap, mon.Bottom - tbH - gap)));
        }
        else
        {
            double tbX = _selDip.Right + gap;
            if (tbX + tbW > mon.Right - gap) tbX = _selDip.X - gap - tbW;
            if (tbX < mon.Left + gap) tbX = Math.Max(mon.Left + gap, mon.Right - tbW - gap);
            double tbY = _selDip.Height >= tbH ? _selDip.Y + (_selDip.Height - tbH) / 2 : _selDip.Y;
            tbY = Math.Clamp(tbY, mon.Top + gap, Math.Max(mon.Top + gap, mon.Bottom - tbH - gap));
            Canvas.SetLeft(Toolbar, tbX);
            Canvas.SetTop(Toolbar, tbY);
        }

        ExportBox.UpdateLayout();
        var exSize = ChromeScale.LayoutSize(ExportBox);
        double exW = exSize.Width, exH = exSize.Height;
        Canvas.SetLeft(ExportBox, Math.Clamp(
            Math.Round(mon.Left + (mon.Width - exW) / 2),
            mon.Left + gap, Math.Max(mon.Left + gap, mon.Right - exW - gap)));
        Canvas.SetTop(ExportBox, mon.Top + 18);

        if (OptionBar.Visibility == Visibility.Visible) PositionOptionBar(mon);
    }

    private void PositionOptionBar(WpfRect? monOpt = null)
    {
        if (OptionBar.Visibility != Visibility.Visible) return;
        var mon = monOpt ?? GetUiMonitorDip();
        OptionBar.UpdateLayout();
        var size = ChromeScale.LayoutSize(OptionBar);
        double w = size.Width, h = size.Height;
        const double gap = 8;

        double x = _selDip.X;
        double y = _selDip.Bottom + gap;
        if (y + h > mon.Bottom - gap) y = _selDip.Y - gap - h;
        if (y < mon.Top + gap) y = mon.Bottom - h - gap;

        x = Math.Clamp(x, mon.Left + gap, Math.Max(mon.Left + gap, mon.Right - w - gap));
        y = Math.Clamp(y, mon.Top + gap, Math.Max(mon.Top + gap, mon.Bottom - h - gap));
        Canvas.SetLeft(OptionBar, x);
        Canvas.SetTop(OptionBar, y);
    }

    private static void PlaceTopCenter(FrameworkElement el, WpfRect mon, double topInset)
    {
        const double gap = 6;
        double w = el.ActualWidth, h = el.ActualHeight;
        double x = Math.Round(mon.Left + (mon.Width - w) / 2);
        double y = mon.Top + topInset;
        Canvas.SetLeft(el, Math.Clamp(x, mon.Left + gap, Math.Max(mon.Left + gap, mon.Right - w - gap)));
        Canvas.SetTop(el, Math.Clamp(y, mon.Top + gap, Math.Max(mon.Top + gap, mon.Bottom - h - gap)));
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBanner.Visibility = Visibility.Visible;
        ToastBanner.UpdateLayout();
        var mon = GetUiMonitorDip();
        PlaceTopCenter(ToastBanner, mon, 64);

        _toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _toastTimer.Stop();
        _toastTimer.Tick -= HideToast;
        _toastTimer.Tick += HideToast;
        _toastTimer.Start();
    }

    private void HideToast(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        ToastBanner.Visibility = Visibility.Collapsed;
    }

    // ===================== Text editing =====================

    private void OnTextEditRequested(TextItem item, bool isNew)
    {
        if (_scene == null) return;
        _textEditing = true;
        _editingItem = item;
        _editBeforeText = item.Text;
        _editIsNewItem = isNew;
        _editCommitted = false;
        item.Measure();

        double scaleY = SceneViewScale().sy;
        // The TextItem renders the real glyphs (ribbon, shadow, colour); the TextBox above it
        // only contributes the caret and the selection highlight.
        var box = new TextBox
        {
            Text = ToTextBoxText(item.Text),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = 8,
            FontFamily = new FontFamily(item.FontFamily),
            FontSize = Math.Max(8, item.FontSize * scaleY),
            FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal,
            Background = Brushes.Transparent,
            Foreground = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            CaretBrush = Brushes.White,
            SelectionBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 0x2F, 0x6F, 0xED)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = ToWpfTextAlignment(item.TextAlignment),
        };
        _textBox = box;

        PlaceTextBox();
        Panel.SetZIndex(box, 1000);
        Root.Children.Add(box);

        box.TextChanged += (_, _) =>
        {
            if (_editingItem == null) return;
            _editingItem.Text = NormalizeEditorText(box.Text);
            _editingItem.Measure();
            _scene?.RaiseChanged();
            PlaceTextBox();
        };
        box.LostFocus += (_, _) => CommitTextEdit();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                CommitTextEdit();
            }
        };

        box.Focus();
        box.SelectAll();
    }

    private void PlaceTextBox()
    {
        if (_textBox == null || _editingItem == null) return;
        var size = _editingItem.Measure();
        var (sx, sy) = SceneViewScale();
        _textBox.Width = Math.Max(8, size.Width * sx + 4);
        _textBox.Height = Math.Max(16, size.Height * sy + 4);
        if (_canvas != null && _canvas.ActualWidth > 0)
        {
            var p = _canvas.ScenePointToWpf(_editingItem.Position);
            Canvas.SetLeft(_textBox, _selDip.X + p.X);
            Canvas.SetTop(_textBox, _selDip.Y + p.Y);
        }
        else
        {
            Canvas.SetLeft(_textBox, _selDip.X + _editingItem.Position.X * sx);
            Canvas.SetTop(_textBox, _selDip.Y + _editingItem.Position.Y * sy);
        }
    }

    private (double sx, double sy) SceneViewScale()
    {
        if (_canvas != null && _canvas.ActualWidth > 0 && _scene is { Width: > 0, Height: > 0 })
        {
            var local = _canvas.SceneRectToWpf(new SKRect(0, 0, _scene.Width, _scene.Height));
            return (local.Width / _scene.Width, local.Height / _scene.Height);
        }
        return (_selDip.Width / Math.Max(1, _scene?.Width ?? 1), _selDip.Height / Math.Max(1, _scene?.Height ?? 1));
    }

    private static string NormalizeEditorText(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string ToTextBoxText(string text)
        => NormalizeEditorText(text).Replace("\n", Environment.NewLine);

    private static TextAlignment ToWpfTextAlignment(TextAlignmentMode alignment) => alignment switch
    {
        TextAlignmentMode.Center => TextAlignment.Center,
        TextAlignmentMode.Right => TextAlignment.Right,
        _ => TextAlignment.Left,
    };

    private void CommitTextEdit()
    {
        if (_editCommitted || _editingItem == null || _scene == null) return;
        _editCommitted = true;
        var item = _editingItem;
        var box = _textBox;

        item.Text = NormalizeEditorText(box?.Text ?? item.Text);
        if (_editIsNewItem)
        {
            _scene.Items.Remove(item);
            if (!string.IsNullOrWhiteSpace(item.Text))
            {
                _scene.Apply(new AddItemAction(item));
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(item.Text))
            {
                _scene.Apply(new RemoveItemAction(item));
            }
            else if (item.Text != _editBeforeText)
            {
                var after = (TextItem)item.Clone();
                var before = (TextItem)item.Clone();
                before.Text = _editBeforeText;
                _scene.Apply(new ModifyItemAction(item, before, after));
            }
        }

        if (box != null) Root.Children.Remove(box);
        _textBox = null;
        _editingItem = null;
        _textEditing = false;
        _scene.RaiseChanged();
        _canvas?.Focus();
    }

    // ===================== OCR / eyedropper / pin =====================

    private bool _pickingColor;

    /// <summary>Reads the text out of the selection with the OCR engine that ships with Windows.</summary>
    private async void RunOcr()
    {
        if (_scene is null) return;

        if (!OcrService.IsAvailable)
        {
            var installed = OcrService.InstalledLanguages();
            ShowToast(installed.Count == 0
                ? "No OCR language pack is installed — add one in Windows language settings"
                : "OCR is unavailable on this machine");
            return;
        }

        ShowToast("Reading text…");
        try
        {
            var background = _scene.Background;
            if (background is null) return;

            // If the user added blur items to redact sensitive parts, respect the redaction.
            // Otherwise, recognize against the clean background without decorative drawings
            // (pen strokes, arrows, boxes, step markers) degrading OCR recognition.
            bool hasBlurs = _scene.Items.Any(it => it is BlurItem);
            SKBitmap bmpToRecognize;
            bool disposeBmp = false;

            if (hasBlurs)
            {
                // Bake against the *real* scene first. A blur stores the pixels underneath it, and
                // baking it on a stripped-down copy captured a snapshot that was missing whatever
                // shapes sat below — so the editor then showed a blur of the wrong content, and the
                // corruption persisted because the baked bitmap is shared with the real item.
                SceneRenderer.BakeDirtyBlurs(_scene);

                // Then render the same scene with everything but the blurs filtered out, so pen
                // strokes, arrows and step markers do not degrade recognition. Reusing the scene
                // (rather than a second Scene sharing its background) also avoids the cached
                // SKImage that copy would have allocated and never released.
                var previousFilter = _scene.HitFilter;
                _scene.HitFilter = it => it is BlurItem;
                try
                {
                    bmpToRecognize = SceneRenderer.RenderToBitmap(_scene, bakeBlurs: false);
                    disposeBmp = true;
                }
                finally
                {
                    _scene.HitFilter = previousFilter;
                }
            }
            else
            {
                bmpToRecognize = background;
            }

            try
            {
                var result = await OcrService.RecognizeAsync(bmpToRecognize);
                if (result is null)
                {
                    ShowToast("OCR is unavailable on this machine");
                    return;
                }
                if (!result.HasText)
                {
                    ShowToast("No text found in the selection");
                    return;
                }

                try { Clipboard.SetText(result.Text); }
                catch
                {
                    ShowToast("Could not write to the clipboard");
                    return;
                }
                ShowToast($"Copied {result.Text.Length} characters ({result.Language})");
            }
            finally
            {
                if (disposeBmp) bmpToRecognize.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OCR failed.");
            ShowToast("OCR failed");
        }
    }

    /// <summary>Arms or disarms the eyedropper: the next click reports the colour under the cursor.</summary>
    private void ToggleColorPicker()
    {
        _pickingColor = !_pickingColor;
        if (_canvas is not null) _canvas.PickingColor = _pickingColor;
        Cursor = _pickingColor ? Cursors.Cross : Cursors.Arrow;
        if (_pickingColor) ShowToast("Click a pixel to copy its colour");
        BuildExportBar();
    }

    /// <summary>Reads the picked pixel from the untouched capture, copies it, and adopts it.</summary>
    private void OnColorPicked(SKPoint p)
    {
        var background = _scene?.Background;
        if (background is null || _canvas is null) return;

        int x = Math.Clamp((int)Math.Round(p.X), 0, background.Width - 1);
        int y = Math.Clamp((int)Math.Round(p.Y), 0, background.Height - 1);
        var color = background.GetPixel(x, y);

        _pickingColor = false;
        _canvas.PickingColor = false;
        Cursor = Cursors.Arrow;

        var display = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
        // Adopt the colour as the annotation colour, so the next shape uses what was just picked.
        _style.StrokeColor = InteractiveCanvas.HexFromColor(color);
        try { Clipboard.SetText(display); }
        catch { /* the clipboard was busy; the toast still reports the value */ }

        ShowToast($"Copied {display}");
        BuildExportBar();
        BuildOptionBar();
    }

    /// <summary>Keep the snip on screen as an always-on-top pin, then close the overlay.</summary>
    private void PinToScreen()
    {
        if (_scene is null) return;
        try
        {
            // Render to a frozen BitmapSource with no Skia backing, so the pin is the only thing
            // that keeps the pixels alive once the overlay closes.
            using var bmp = SceneRenderer.RenderToBitmap(_scene);
            using var data = ImageExporter.Encode(bmp, ImageFormat.Png, 100);
            var frame = BitmapFrame.Create(new MemoryStream(data.ToArray()),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();

            var placementOnDesktop = new WpfRect(
                Left + _selDip.X,
                Top + _selDip.Y,
                _selDip.Width,
                _selDip.Height);
            var pin = new PinnedWindow(frame, placementOnDesktop);
            pin.Show();
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not pin the snip.");
            ShowToast("Could not pin the snip");
        }
    }

    // ===================== Export =====================

    private void CopyAndClose()
    {
        if (_scene == null) return;
        try
        {
            using var bmp = SceneRenderer.RenderToBitmap(_scene);
            ImageExporter.CopyToClipboard(bmp);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not copy the image: " + ex.Message, "WhisperPlus", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Save()
    {
        if (_scene == null) return;
        try
        {
            var format = OutputFormat?.ToLowerInvariant() switch
            {
                "jpg" or "jpeg" => ImageFormat.Jpeg,
                "webp" => ImageFormat.Webp,
                _ => ImageFormat.Png,
            };
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}",
                Filter = "PNG image|*.png|JPEG image|*.jpg|WebP image|*.webp",
                FilterIndex = format switch { ImageFormat.Jpeg => 2, ImageFormat.Webp => 3, _ => 1 },
                InitialDirectory = Directory.Exists(SaveDirectory)
                    ? SaveDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                DefaultExt = "." + ImageExporter.Extension(format),
            };
            if (dlg.ShowDialog(this) != true) return;

            var chosen = Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                ".webp" => ImageFormat.Webp,
                _ => ImageFormat.Png,
            };
            using var bmp = SceneRenderer.RenderToBitmap(_scene);
            using var data = ImageExporter.Encode(bmp, chosen, Quality);
            ImageExporter.SaveEncoded(data, dlg.FileName);
            var chosenDir = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrWhiteSpace(chosenDir))
            {
                SaveDirectory = chosenDir;
                SaveDirectoryChanged?.Invoke(chosenDir);
            }
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not save the image: " + ex.Message, "WhisperPlus", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===================== Keyboard =====================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        // Esc while typing ends the edit; a second Esc closes the overlay.
        if (_textEditing)
        {
            CommitTextEdit();
            e.Handled = true;
            return;
        }
        Close();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_textEditing) return;
        if (_phase != Phase.Edit) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (ctrl && e.Key == Key.Z) { if (shift) _scene?.Redo(); else _scene?.Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { _scene?.Redo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.C) { CopyAndClose(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { Save(); e.Handled = true; return; }
        if (e.Key is Key.Delete or Key.Back) { _canvas?.DeleteSelected(); e.Handled = true; return; }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && _canvas?.SelectedItem != null)
        {
            float step = ctrl ? 1f : shift ? 30f : 10f;
            float dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0f;
            float dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0f;
            _canvas.NudgeSelection(dx, dy);
            e.Handled = true;
            return;
        }

        if (_canvas == null) return;
        EditorTool? tool = e.Key switch
        {
            Key.V => EditorTool.Select,
            Key.R => EditorTool.Rectangle,
            Key.O => EditorTool.Ellipse,
            Key.A => EditorTool.Arrow,
            Key.L => EditorTool.Line,
            Key.P => EditorTool.Pen,
            Key.H => EditorTool.Highlight,
            Key.T => EditorTool.Text,
            Key.S => EditorTool.Step,
            Key.B => EditorTool.Blur,
            _ => null,
        };
        if (tool.HasValue)
        {
            _canvas.Tool = tool.Value;
            e.Handled = true;
        }
    }

    // ===================== Teardown =====================

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
        SessionEnded?.Invoke();
    }

    /// <summary>
    /// Releases everything that scales with screen size. Called from <see cref="OnClosed"/>;
    /// safe to call twice.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _toastTimer?.Stop();
        _toastTimer = null;

        if (_textBox != null)
        {
            Root.Children.Remove(_textBox);
            _textBox = null;
        }
        _editingItem = null;

        DetachScene();

        // Detach the message hook before the HWND goes away.
        _source?.RemoveHook(OverlayInputHook);
        _source = null;

        // The frozen desktop: a GDI bitmap plus its BitmapSource copy.
        ScreenImage.Source = null;
        _screenshot.Dispose();

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
    }
}
