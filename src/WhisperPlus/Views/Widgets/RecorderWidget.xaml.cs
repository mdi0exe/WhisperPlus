using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using WhisperPlus.Services;

namespace WhisperPlus.Views.Widgets;

/// <summary>
/// Compact, borderless, "always-on-top" floating audio recorder. Designed to be kept
/// visible while reading PDFs or browsing: the user pins it (or uses the global hotkeys)
/// and watches the live waveform as they record.
///
/// <para>The widget is created on demand and reused across sessions. It owns its buttons'
/// click handlers plus the global-hotkey commands; the underlying capture logic lives in
/// <see cref="AudioRecorder"/>. Position is draggable via the card border.</para>
/// </summary>
public partial class RecorderWidget : Window
{
    private readonly AudioRecorder _recorder;
    private readonly ISettingsService _settings;

    public RecorderWidget(AudioRecorder recorder, ISettingsService settings)
    {
        _recorder = recorder;
        _settings = settings;
        InitializeComponent();
        DataContext = this;

        Waveform.Aggregator = _recorder.Levels;
        _recorder.StateChanged += OnStateChanged;
        _recorder.RecordingSaved += OnRecordingSaved;
        _recorder.RecordingDiscarded += OnRecordingDiscarded;
        _recorder.RecordingCancelled += OnRecordingCancelled;

        UpdateStatusText(_recorder.State);
        PositionAtTopRight();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
    }

    private System.Windows.Threading.DispatcherTimer? _hideTimer;

    private void CancelHideTimer()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
    }

    // ---- Global-hotkey command surface (called by App's hotkey handler) ----
    public void CommandStart() => Dispatcher.Invoke(() => StartButton_Click(this, new RoutedEventArgs()));
    public void CommandPause() => Dispatcher.Invoke(() => PauseButton_Click(this, new RoutedEventArgs()));
    public void CommandStop() => Dispatcher.Invoke(() => StopButton_Click(this, new RoutedEventArgs()));

    // ---- Button handlers ----
    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        CancelHideTimer();
        var s = _settings.Current;
        try
        {
            _recorder.Start(s.RecordingDeviceIndex, s.RecordingOutputDirectory, s.RecordingFormat, s.SaveRecordings);
            Waveform.Start();
        }
        catch (Exception ex)
        {
            // Surf the failure in the status line; logged by the recorder.
            StatusText = $"Mic error: {ex.Message}";
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.State == RecordingState.Paused) _recorder.Resume();
        else _recorder.Pause();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _recorder.Stop();
    }

    /// <summary>
    /// Dismisses the widget. Anything being recorded is cancelled rather than stopped — the
    /// partial file is deleted and nothing is written anywhere. Closing is "never mind", not
    /// "stop and keep"; the stop button is still there for keeping it.
    ///
    /// <para>The window is hidden rather than closed: it is a singleton reused by the tray menu
    /// and the recorder hotkeys, and a closed WPF window cannot be shown again.</para>
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CancelHideTimer();
        _recorder.Cancel();
        Waveform.Stop();
        UpdateStatusText(RecordingState.Idle);
        Hide();
    }

    private void OnStateChanged(RecordingState state)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateStatusText(state);
            if (state == RecordingState.Recording) Waveform.Start();
            else if (state == RecordingState.Paused) Waveform.Stop();
        });
    }

    private void OnRecordingSaved(string path)
    {
        // Briefly show the saved filename, then revert to idle.
        ShowThenHide($"Saved: {System.IO.Path.GetFileName(path)}");
    }

    /// <summary>
    /// The recording ended without being written anywhere. Said out loud rather than shown as a
    /// plain "Ready", so the user is never left wondering which folder to look in.
    /// </summary>
    private void OnRecordingDiscarded()
    {
        ShowThenHide("Not saved (saving is off)");
    }

    /// <summary>
    /// The recording was thrown away by the close button. Nothing is flashed on the way out —
    /// the user has already dismissed the widget, so putting it back on screen to report that
    /// would undo the gesture.
    /// </summary>
    private void OnRecordingCancelled()
    {
        Dispatcher.Invoke(() =>
        {
            Waveform.Stop();
            UpdateStatusText(RecordingState.Idle);
            Hide();
        });
    }

    /// <summary>Flashes a closing message, then returns the widget to idle and hides it.</summary>
    private void ShowThenHide(string message)
    {
        Dispatcher.Invoke(() =>
        {
            CancelHideTimer();
            StatusText = message;
            _hideTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _hideTimer.Tick += (_, _) =>
            {
                CancelHideTimer();
                if (_recorder.State != RecordingState.Idle) return;
                UpdateStatusText(_recorder.State);
                Hide();
            };
            _hideTimer.Start();
            Waveform.Stop();
        });
    }

    /// <summary>
    /// Pushes one recorder state onto every piece of widget chrome: the label, the state dot's
    /// color (and, through <see cref="State"/>, whether it pulses), and the play/pause glyph.
    /// </summary>
    private void UpdateStatusText(RecordingState state)
    {
        StatusText = state switch
        {
            RecordingState.Recording => "Recording",
            RecordingState.Paused => "Paused",
            RecordingState.Stopping => "Saving…",
            _ => "Ready to record",
        };

        StateBrush = state switch
        {
            RecordingState.Recording => RecordingBrush,
            RecordingState.Paused => PausedBrush,
            RecordingState.Stopping => PausedBrush,
            _ => IdleBrush,
        };

        // The button is a toggle, so its glyph has to say what pressing it will do next.
        PauseButton.Icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = state == RecordingState.Paused
                ? Wpf.Ui.Controls.SymbolRegular.Play24
                : Wpf.Ui.Controls.SymbolRegular.Pause24,
        };

        State = state.ToString();
    }

    private static readonly System.Windows.Media.Brush IdleBrush = Frozen("#8CFFFFFF");
    private static readonly System.Windows.Media.Brush RecordingBrush = Frozen("#FF6B7E");
    private static readonly System.Windows.Media.Brush PausedBrush = Frozen("#FFC24B");

    private static System.Windows.Media.Brush Frozen(string hex)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // ---- Draggable by the card background (window has no title bar) ----
    private void CardBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch (InvalidOperationException) { /* DragMove can throw if button not pressed */ }
    }

    private void PositionAtTopRight()
    {
        // Place near the top-right of the primary screen, leaving margin for the title-bar-less card.
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 24;
        Top = work.Top + 24;
    }

    /// <summary>Simple view-state property bound in XAML (status label).</summary>
    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(RecorderWidget),
            new PropertyMetadata("Ready to record"));
    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    /// <summary>The <see cref="RecordingState"/> as a string; drives the state dot's pulse.</summary>
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(string), typeof(RecorderWidget),
            new PropertyMetadata(nameof(RecordingState.Idle)));
    public string State
    {
        get => (string)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Color of the state dot next to the status label.</summary>
    public static readonly DependencyProperty StateBrushProperty =
        DependencyProperty.Register(nameof(StateBrush), typeof(System.Windows.Media.Brush), typeof(RecorderWidget),
            new PropertyMetadata(IdleBrush));
    public System.Windows.Media.Brush StateBrush
    {
        get => (System.Windows.Media.Brush)GetValue(StateBrushProperty);
        set => SetValue(StateBrushProperty, value);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CancelHideTimer();
        _recorder.Stop();
        _recorder.StateChanged -= OnStateChanged;
        _recorder.RecordingSaved -= OnRecordingSaved;
        _recorder.RecordingDiscarded -= OnRecordingDiscarded;
        _recorder.RecordingCancelled -= OnRecordingCancelled;
        base.OnClosing(e);
    }
}
