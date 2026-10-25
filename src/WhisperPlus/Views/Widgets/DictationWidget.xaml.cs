using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NAudio.Wave;
using Serilog;
using Wpf.Ui.Controls;
using WhisperPlus.Models;
using WhisperPlus.Services;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
// System.Windows.Controls and Wpf.Ui.Controls both contribute many types; the unqualified ones
// used here (MenuItem for the chip's ContextMenu) resolve to the standard WPF controls.
using MenuItem = System.Windows.Controls.MenuItem;

namespace WhisperPlus.Views.Widgets;

/// <summary>
/// Bottom-center floating dictation widget. The hotkey starts/stops a capture session: audio is
/// recorded into a 16 kHz mono float buffer and handed to a <see cref="StreamingDictationSession"/>,
/// which identifies the language, routes to the model mapped to it, and transcribes while the user
/// is still speaking. The result is pasted into the focused field via <see cref="TextInjector"/>.
/// The widget shows a live waveform, the detected language as a clickable 2-letter chip, the
/// status (Idle / Listening / Processing / Done), and the text as it is recognized.
/// </summary>
public partial class DictationWidget : Window
{
    private const int CaptureSampleRate = 16000; // required by every engine
    private const int CaptureChannels = 1;

    /// <summary>Characters of live text shown; the widget is a pill, not a document view.</summary>
    private const int PreviewChars = 64;

    private readonly WhisperService _whisper;
    private readonly VoskService _vosk;
    private readonly NemotronService _nemotron;
    private readonly BarkService _bark;
    private readonly TextInjector _injector;
    private readonly ISettingsService _settings;
    private readonly LanguageDetector _detector;
    private readonly LanguageRouter _router;
    private readonly TranscriptHistory _history;

    private WaveInEvent? _capture;
    private StreamingDictationSession? _session;

    /// <summary>
    /// Which engine the session that is currently recording will be transcribed by. Latched when
    /// listening starts so switching engines mid-session can't hand the buffer to an engine that
    /// was never loaded, and so the right one is unloaded afterwards. Kept in sync when routing
    /// switches the target.
    /// </summary>
    private string _sessionEngine = SpeechEngines.Whisper;

    /// <summary>
    /// When a session is launched from a preset shortcut, these pin the engine + model for that
    /// session only, overriding the active settings. Cleared when the session ends, so the next
    /// hotkey reverts to the user's chosen defaults. Null when no preset override is in effect.
    /// </summary>
    private string? _overrideEngine;
    private string? _overrideModel;

    /// <summary>
    /// Whether the session that is recording leaves the language until the hotkey is pressed again.
    /// Latched at start like <see cref="_sessionEngine"/>, and read by the stop path: with detection
    /// deferred, identifying the language and loading its model happen *after* recording, so those are
    /// the only interesting things on screen during it, and "Transcribing…" would be a lie for the
    /// several seconds a large model takes to open.
    /// </summary>
    private bool _sessionDeferredDetection;

    /// <summary>
    /// Set by the close button. Read after the transcription completes, because the user can
    /// dismiss the widget while the tail is still decoding and the transcript must then be
    /// dropped rather than pasted into whatever they moved on to.
    /// </summary>
    private bool _cancelRequested;

    /// <summary>True between the stop and the end of <see cref="StopAndTranscribe"/>.</summary>
    private bool _transcribing;

    /// <summary>Which layout is currently applied; null until the first one is.</summary>
    private bool? _compact;

    public DictationWidget(
        WhisperService whisper,
        VoskService vosk,
        NemotronService nemotron,
        BarkService bark,
        TextInjector injector,
        ISettingsService settings,
        LanguageDetector detector,
        LanguageRouter router,
        TranscriptHistory history)
    {
        _whisper = whisper;
        _vosk = vosk;
        _nemotron = nemotron;
        _bark = bark;
        _injector = injector;
        _settings = settings;
        _detector = detector;
        _router = router;
        _history = history;
        InitializeComponent();
        DataContext = this;

        Waveform.Aggregator = _levelAggregator;
        ApplyWidgetSize(); // also positions the window, whose size it decides
        SetStatus(DictationState.Idle);

        // The size is a live setting: switching it on the Appearance page re-lays out the widget
        // straight away rather than at the next restart.
        _settings.Changed += OnSettingsChanged;
        IsVisibleChanged += OnWidgetIsVisibleChanged;
    }

    private void OnWidgetIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            PlayFadeInAnimation();
        }
    }

    private void PlayFadeInAnimation()
    {
        var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var slideUp = new DoubleAnimation(8.0, 0.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        CardBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        if (CardBorder.RenderTransform is TranslateTransform tt)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }
    }

    /// <summary>
    /// Fades the card out and hides the window.
    /// </summary>
    /// <param name="durationMs">Length of the fade.</param>
    /// <param name="canHide">
    /// Asked once the animation has finished, immediately before the window is hidden. The hotkey
    /// stays live for the whole fade, so the user can start the next session while it runs; hiding
    /// then would make the widget vanish mid-recording. Returning false restores the card and
    /// leaves the window visible.
    /// </param>
    /// <returns>True when the window was actually hidden.</returns>
    private async Task<bool> FadeOutAndHideAsync(int durationMs = 150, Func<bool>? canHide = null)
    {
        var tcs = new TaskCompletionSource<bool>();
        var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) => tcs.TrySetResult(true);
        CardBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);

        await Task.WhenAny(tcs.Task, Task.Delay(durationMs + 50));

        // No await between the check and Hide(), so the two cannot be split by a session starting.
        if (canHide is not null && !canHide())
        {
            RestoreCardVisual();
            return false;
        }

        Hide();
        RestoreCardVisual();
        return true;
    }

    /// <summary>Clears the fade animation and puts the card back at full opacity, untranslated.</summary>
    private void RestoreCardVisual()
    {
        CardBorder.BeginAnimation(UIElement.OpacityProperty, null);
        CardBorder.Opacity = 1.0;
        if (CardBorder.RenderTransform is TranslateTransform tt)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = 0;
        }
    }

    // ---- Draggable by the card background (window has no title bar) ----
    private void CardBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            PositionBottomCenter();
            e.Handled = true;
            return;
        }

        if (e.OriginalSource is DependencyObject dep)
        {
            var parent = dep;
            while (parent != null && !ReferenceEquals(parent, CardBorder))
            {
                if (parent is System.Windows.Controls.Button || ReferenceEquals(parent, LanguageChip))
                    return;
                parent = VisualTreeHelper.GetParent(parent);
            }
        }

        try { DragMove(); }
        catch (InvalidOperationException) { /* DragMove can throw if mouse button was already released */ }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
    }

    private readonly WaveformAggregator _levelAggregator = new();

    private enum DictationState { Idle, Listening, Processing, Done }

    private CancellationTokenSource? _hideCts;

    private void CancelHideTimer()
    {
        if (_hideCts is not null)
        {
            try { _hideCts.Cancel(); } catch { }
            try { _hideCts.Dispose(); } catch { }
            _hideCts = null;
        }
    }

    // ---- Hotkey entry point (toggles listening) ----
    public void Toggle()
    {
        Dispatcher.Invoke(() =>
        {
            if (_transcribing) return;
            CancelHideTimer();
            if (_capture is null) StartListening();
            else StopAndTranscribe();
        });
    }

    /// <summary>Starts listening without changing an already active session.</summary>
    public void StartDictation()
    {
        Dispatcher.Invoke(() =>
        {
            if (_transcribing) return;
            CancelHideTimer();
            if (_capture is null) StartListening();
        });
    }

    /// <summary>Stops an active session and starts transcription.</summary>
    public void StopDictation()
    {
        Dispatcher.Invoke(() =>
        {
            if (_capture is not null) StopAndTranscribe();
        });
    }

    /// <summary>True while a capture session is recording (a session that hasn't transcribed yet).</summary>
    public bool IsSessionActive => _capture is not null;

    /// <summary>
    /// Starts a dictation session pinned to a specific engine + model for this session only —
    /// used by per-engine/model preset shortcuts. Ignores the call if a session is already
    /// running so two shortcuts can't collide. The override is cleared when the session ends.
    /// </summary>
    public void StartWithPreset(string engine, string model)
    {
        Dispatcher.Invoke(() =>
        {
            if (_transcribing) return;
            CancelHideTimer();
            if (_capture is not null) return;
            _overrideEngine = engine;
            _overrideModel = model;
            StartListening();
        });
    }

    /// <summary>
    /// Dismisses the widget and abandons whatever it was doing. A session that is still recording
    /// is dropped where it stands, and one that is already transcribing is cancelled and its
    /// transcript discarded: nothing is pasted, nothing reaches the clipboard, and nothing is
    /// added to the history. The models are unloaded on the way out, as after a normal session.
    ///
    /// <para>The window is hidden rather than closed — it is a singleton the hotkeys and the tray
    /// menu resolve, and a closed WPF window cannot be shown again.</para>
    /// </summary>
    public void CancelSession()
    {
        Dispatcher.Invoke(() =>
        {
            CancelHideTimer();
            _cancelRequested = true;

            // A transcription in flight owns the teardown: it has to finish unwinding before the
            // models can be unloaded, so it is only told to stop here and hides itself after.
            if (_transcribing)
            {
                _session?.Cancel();
                _ = FadeOutAndHideAsync(100);
                return;
            }

            var session = _session;
            CleanupCapture();
            Waveform.Stop();
            session?.Cancel();
            DisposeSession(session);

            PreviewText = "";
            ResetLanguageChip();
            SetStatus(DictationState.Idle);
            _ = FadeOutAndHideAsync(100);

            // The decode loop may still be inside a native call on the very models UnloadEngines
            // frees; freeing them underneath it is a use-after-free that kills the process with no
            // managed stack. So the unload waits for the pump to return — off the UI thread, since
            // the widget is already gone and nothing here needs to block on it.
            _ = UnloadEnginesAfterAsync(session);
            _cancelRequested = false;
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CancelSession();

    /// <summary>
    /// The languages of recent dictations, most recent first — a weak tie-breaking prior for language
    /// identification, since a bilingual user does not dictate 50/50 and the language they were using
    /// a minute ago is more likely than the one they were not.
    ///
    /// <para>Deliberately weak: <see cref="LanguagePrior"/> caps the whole prior's influence well below
    /// what one clean second of audio is worth, which is what stops this from becoming a feedback loop
    /// — history feeds the prior, the prior nudges the routing, and the routing is what gets written
    /// back to history.</para>
    /// </summary>
    private IReadOnlyList<string?> RecentDictationLanguages()
    {
        var transcripts = _history.Transcripts;
        var codes = new List<string?>(transcripts.Count);
        foreach (var entry in transcripts) codes.Add(entry.LanguageCode);
        return codes;
    }

    private void StartListening()
    {
        _cancelRequested = false;

        // A preset shortcut pins one exact engine+model, so routing sits this session out.
        var usePreset = _overrideEngine is not null && _overrideModel is not null;
        var target = usePreset
            ? new RouteTarget(SpeechEngines.Normalize(_overrideEngine!), _overrideModel!, null)
            : _router.DefaultTarget();

        _sessionEngine = target.Engine;

        // Only the cheap on-disk check happens up front, so the hotkey opens the mic straight
        // away. Reading a multi-gigabyte model would otherwise freeze the UI for seconds
        // before recording even started, and the first words got lost.
        if (!_router.IsInstalled(target))
        {
            SetStatus(DictationState.Idle, $"Model '{target.Model}' not downloaded");
            ResetLanguageChip();
            return;
        }

        var routingEnabled = !usePreset && _router.IsRoutingEnabled;
        var candidates = routingEnabled ? _router.ActiveLanguages : System.Array.Empty<string>();

        // Only routing has a language to defer: without it there is nothing detecting anything.
        var deferDetection = routingEnabled && _settings.Current.DetectLanguageAfterSpeech;
        _sessionDeferredDetection = deferDetection;

        // The chip is shown whenever a language is in play at all, not only when routing is on: with
        // routing off it displays the language the user chose on the Language page (or "auto") and
        // still opens the quick switcher, so changing language mid-session doesn't mean going back
        // to settings. A preset pins one exact engine+model, so it has no language to show.
        var showChip = routingEnabled || (!usePreset && _settings.Current.ActiveLanguages.Count > 0);
        var initialChip = routingEnabled ? "--" : target.LanguageCode ?? "auto";

        try
        {
            _levelAggregator.Configure(CaptureSampleRate, CaptureChannels);
            _levelAggregator.SetEnabled(true);
            _levelAggregator.Reset();
            Waveform.Start();

            _capture = new WaveInEvent
            {
                DeviceNumber = AudioDeviceService.ResolveDeviceNumber(_settings.Current.RecordingDeviceIndex),
                WaveFormat = new WaveFormat(CaptureSampleRate, CaptureChannels),
                BufferMilliseconds = 50,
            };
            _capture.DataAvailable += OnCaptureDataAvailable;

            // The session loads the model on a background thread now that audio is about to flow,
            // so the wait overlaps with the user speaking instead of happening before or after —
            // and, with routing on, it identifies the language and swaps the model mid-sentence.
            _session = new StreamingDictationSession(_whisper, _vosk, _nemotron, _bark, _detector, _router,
                new SessionOptions(target, routingEnabled, candidates, _settings.Current.LiveTranscription,
                    CaptureSampleRate, RecentLanguages: RecentDictationLanguages(),
                    DeferDetectionUntilStop: deferDetection));
            _session.TargetChanged += OnSessionTargetChanged;
            _session.PreviewChanged += OnSessionPreviewChanged;
            _session.StatusChanged += OnSessionStatusChanged;

            PreviewText = "";
            ResetLanguageChip(showChip ? initialChip : null);
            _session.Start();

            _capture.StartRecording();

            // Say so while recording, because the chip staying on "--" and (with live transcription)
            // the empty preview are otherwise indistinguishable from detection having failed.
            SetStatus(DictationState.Listening,
                deferDetection ? "Listening… · language when you stop" : null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start dictation capture.");
            SetStatus(DictationState.Idle, "Mic error");
            CleanupCapture();
        }
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Convert 16-bit PCM → float32 and hand it to the session. Also feed the level meter.
        var sampleCount = e.BytesRecorded / 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            short s = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }
        _session?.Append(samples);
        _levelAggregator.Process(new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded));
    }

    private async void StopAndTranscribe()
    {
        var session = _session;
        _transcribing = true;
        CleanupCapture();
        Waveform.Stop();
        SetStatus(DictationState.Processing,
            _sessionDeferredDetection ? "Identifying language…" : null);

        var cancelled = false;
        try
        {
            if (session is null)
            {
                SetStatus(DictationState.Idle);
                return;
            }

            // Collect the live transcription. The committed part was decoded while the user was
            // talking, so usually only a short tail is left to finish here.
            var text = await session.FinishAsync();
            cancelled = _cancelRequested;

            if (cancelled)
            {
                // The widget was closed while this was decoding. Whatever came back is dropped:
                // not pasted, not copied, not remembered.
                Log.Information("Dictation cancelled; transcript discarded.");
            }
            else if (session.ModelLoadFailed)
            {
                SetStatus(DictationState.Idle, "Model failed to load");
                return;
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                var trimmed = text.Trim();
                _history.Add(trimmed, session.DetectedLanguage);

                // Paste into the focused field and leave the transcript on the clipboard, so it
                // can be pasted again if the target app never took it (or took it wrong).
                var pasted = await _injector.PasteAsync(trimmed);
                SetStatus(DictationState.Done, pasted
                    ? $"Pasted: {Truncate(trimmed, 40)}"
                    : $"Copied: {Truncate(trimmed, 40)}");
            }
            else
            {
                SetStatus(DictationState.Done, "Nothing recognized");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Dictation transcription failed.");
            SetStatus(DictationState.Idle, "Transcription error");
            return;
        }
        finally
        {
            DisposeSession(session);
            _transcribing = false;
            _cancelRequested = false;
            _sessionDeferredDetection = false;
        }

        // Keep the result visible just long enough to confirm it, then get out of the
        // way. The next hotkey shows the same window again before recording starts.
        // A cancelled session skips the pause: the widget is already gone.
        if (!cancelled)
        {
            CancelHideTimer();
            var cts = new CancellationTokenSource();
            _hideCts = cts;
            try
            {
                await Task.Delay(900, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cts.IsCancellationRequested || _session is not null || _capture is not null)
                return;
        }

        CancelHideTimer();
        SetStatus(DictationState.Idle);
        PreviewText = "";
        ResetLanguageChip();

        // Latched before the fade: a session started during it overwrites _sessionEngine, and
        // unloading *that* session's engine is not what this path is unwinding.
        var finishedEngine = _sessionEngine;

        // The hotkey is live throughout the fade, so the user can already be recording again by the
        // time it ends. In that case the widget must stay on screen and the engines must stay loaded
        // — disposing the model under a session that has just started is a native crash, not a glitch.
        if (!await FadeOutAndHideAsync(150, () => _session is null && _capture is null)) return;

        UnloadEngines(finishedEngine);
    }

    /// <summary>
    /// Unloads the engines once <paramref name="session"/>'s decode loop has returned. A new session
    /// may have been started in the meantime, in which case the engines are back in use and must be
    /// left alone.
    /// </summary>
    private async Task UnloadEnginesAfterAsync(StreamingDictationSession? session)
    {
        // Taken before the wait: _sessionEngine follows whatever session is running now, and this
        // path is unwinding a specific one.
        var engine = _sessionEngine;

        if (session is not null)
        {
            try { await session.WhenPumpStoppedAsync().ConfigureAwait(true); }
            catch (Exception ex) { Log.Debug(ex, "Session pump ended with an error."); }
        }

        if (_session is null) UnloadEngines(engine);
    }

    /// <summary>
    /// Frees the models between sessions — each engine holds hundreds of MB while loaded, and the
    /// detector holds its own (small) model on top of that. Only ever called once the session that
    /// was using them has finished unwinding, and only for the engine that session used.
    /// </summary>
    private void UnloadEngines(string? engine)
    {
        switch (engine)
        {
            case SpeechEngines.Vosk: _vosk.Unload(); break;
            case SpeechEngines.Nemotron: _nemotron.Unload(); break;
            case SpeechEngines.Bark: _bark.Unload(); break;
            default: _whisper.Unload(); break;
        }
        _detector.Unload();
    }

    private void DisposeSession(StreamingDictationSession? session)
    {
        if (session is null) return;
        session.TargetChanged -= OnSessionTargetChanged;
        session.PreviewChanged -= OnSessionPreviewChanged;
        session.StatusChanged -= OnSessionStatusChanged;
        session.Dispose();
        if (ReferenceEquals(_session, session)) _session = null;
    }

    // ---- Session callbacks (raised from the session's pump thread) ----

    private void OnSessionTargetChanged(RouteTarget target) => Dispatcher.Invoke(() =>
    {
        _sessionEngine = target.Engine;
        // With routing on, no code yet means "still deciding" ("--"); with it off, it means the
        // engine is deciding for itself, which is a settled state and reads as "auto".
        DetectedLanguage = target.LanguageCode ?? (_router.IsRoutingEnabled ? "--" : "auto");
        LanguageChipBrush = ChipDecidedBrush;
        LanguageChipBorder = _session?.IsManualOverride == true
            ? ChipOverriddenBrush
            : Brushes.Transparent;
    });

    private void OnSessionPreviewChanged(string text) => Dispatcher.Invoke(() =>
    {
        PreviewText = TailOf(text, PreviewChars);
    });

    private void OnSessionStatusChanged(string status) => Dispatcher.Invoke(() =>
    {
        // Normally only while listening: the stop path owns the status from then on. A deferred session
        // is the exception, because everything worth saying — the language, the model being loaded for
        // it — is decided after the user stops, and the stop path's own notes (the paste, an error) all
        // come later than these do.
        if (_capture is not null || _sessionDeferredDetection) StatusText = status;
    });

    // ---- Manual language override ----

    /// <summary>
    /// Opens the quick language switcher. The user picks from their active languages and the
    /// session immediately switches: with routing on the mapped model starts loading, and either
    /// way the audio so far is re-transcribed with the chosen language.
    ///
    /// <para>The list works the same whether or not routing is on — that is the point of showing
    /// the chip in both cases. What differs is what "Automatic" means: with routing on it resumes
    /// detection, with it off it hands the choice to the engine, one decode at a time.</para>
    /// </summary>
    private void LanguageChip_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var session = _session;
        if (session is null) return;

        var languages = _settings.Current.ActiveLanguages;
        if (languages.Count == 0) return;

        var menu = new ContextMenu { PlacementTarget = LanguageChip, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };

        var automatic = new MenuItem
        {
            Header = _router.IsRoutingEnabled ? "Automatic  ·  detect it" : "Automatic  ·  let the model decide",
            IsCheckable = false,
        };
        automatic.Click += (_, _) =>
        {
            session.OverrideToAutomatic();
            DetectedLanguage = _router.IsRoutingEnabled ? "--" : "auto";
            LanguageChipBrush = _router.IsRoutingEnabled ? ChipUndecidedBrush : ChipDecidedBrush;
            LanguageChipBorder = ChipOverriddenBrush;
        };
        menu.Items.Add(automatic);
        menu.Items.Add(new Separator());

        foreach (var code in languages)
        {
            var item = new MenuItem
            {
                Header = $"{code}  ·  {LanguageCatalog.DisplayName(code)}",
                IsChecked = code == session.DetectedLanguage,
                IsCheckable = false,
            };
            var chosen = code;
            item.Click += (_, _) =>
            {
                session.OverrideLanguage(chosen);
                DetectedLanguage = chosen;
                LanguageChipBrush = ChipDecidedBrush;
                LanguageChipBorder = ChipOverriddenBrush;
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Chip text once a language is settled, versus while it is still undecided.</summary>
    private static readonly Brush ChipDecidedBrush = Frozen("#F7FFFFFF");
    private static readonly Brush ChipUndecidedBrush = Frozen("#8CFFFFFF");

    /// <summary>Ring drawn around the chip after the user has corrected the language by hand.</summary>
    private static readonly Brush ChipOverriddenBrush = Frozen("#7C9CFF");

    private void ResetLanguageChip(string? code = null)
    {
        DetectedLanguage = code ?? "--";
        // "auto" is a settled choice, not an undecided one, so it reads at full strength.
        LanguageChipBrush = code is null or "--" ? ChipUndecidedBrush : ChipDecidedBrush;
        LanguageChipBorder = Brushes.Transparent;
        LanguageChipVisibility = code is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CleanupCapture()
    {
        if (_capture is not null)
        {
            try { _capture.StopRecording(); } catch { /* ignore */ }
            try { _capture.Dispose(); } catch { /* ignore */ }
            _capture = null;
        }
        _levelAggregator.SetEnabled(false);
        // A preset override applies to one session only — drop it so the next hotkey reverts
        // to the user's active engine/model settings.
        _overrideEngine = null;
        _overrideModel = null;
    }

    private void PositionBottomCenter()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Bottom - Height - 12; // just above the taskbar
    }

    // ---- Size ----

    /// <summary>
    /// Every measurement that differs between the two widget sizes. Kept as one record rather than
    /// as a second XAML file so the layout itself exists once: the compact widget is the same pill
    /// with smaller numbers, and a change to its structure cannot be made to one and not the other.
    /// </summary>
    private sealed record Metrics(
        double WindowWidth,
        double WindowHeight,
        Thickness CardMargin,
        Thickness CardPadding,
        CornerRadius CardCorner,
        double OrbSize,
        Thickness OrbMargin,
        double IconSize,
        Thickness ChipMargin,
        Thickness ChipPadding,
        double ChipMinWidth,
        double ChipFontSize,
        double WaveformHeight,
        double StatusFontSize,
        Thickness StatusMargin,
        double PreviewFontSize,
        double CloseSize,
        Thickness CloseMargin);

    /// <summary>The full pill: room for the status line and the live transcript under it.</summary>
    private static readonly Metrics LargeMetrics = new(
        WindowWidth: 440,
        WindowHeight: 124,
        CardMargin: new Thickness(18, 10, 18, 20),
        CardPadding: new Thickness(16, 12, 16, 12),
        CardCorner: new CornerRadius(24),
        OrbSize: 46,
        OrbMargin: new Thickness(0, 0, 14, 0),
        IconSize: 21,
        ChipMargin: new Thickness(0, 0, 14, 0),
        ChipPadding: new Thickness(10, 5, 10, 5),
        ChipMinWidth: 38,
        ChipFontSize: 12.5,
        WaveformHeight: 26,
        StatusFontSize: 12,
        StatusMargin: new Thickness(0, 5, 0, 0),
        PreviewFontSize: 11,
        CloseSize: 22,
        CloseMargin: new Thickness(10, -4, -6, 0));

    /// <summary>
    /// About a quarter of the area of <see cref="LargeMetrics"/> (224×62 against 440×124), for
    /// keeping the widget up permanently. Only one line of text fits, so the transcript takes the
    /// status line's place while there is one — see <see cref="UpdateTextLines"/>.
    /// </summary>
    private static readonly Metrics SmallMetrics = new(
        WindowWidth: 224,
        WindowHeight: 62,
        CardMargin: new Thickness(9, 5, 9, 10),
        CardPadding: new Thickness(9, 6, 9, 6),
        CardCorner: new CornerRadius(14),
        OrbSize: 28,
        OrbMargin: new Thickness(0, 0, 7, 0),
        IconSize: 13,
        ChipMargin: new Thickness(0, 0, 7, 0),
        ChipPadding: new Thickness(6, 2, 6, 2),
        ChipMinWidth: 24,
        ChipFontSize: 10,
        WaveformHeight: 15,
        StatusFontSize: 10,
        StatusMargin: new Thickness(0, 2, 0, 0),
        PreviewFontSize: 10,
        CloseSize: 16,
        CloseMargin: new Thickness(5, -2, -3, 0));

    /// <summary>
    /// Lays the widget out at the size the user picked. Called at construction and whenever the
    /// setting changes, so switching sizes takes effect without a restart.
    /// </summary>
    private void ApplyWidgetSize()
    {
        var compact = WidgetSizes.Normalize(_settings.Current.DictationWidgetSize) == WidgetSizes.Small;
        if (_compact == compact) return; // nothing to redo: every other setting saves through here too
        _compact = compact;

        var m = compact ? SmallMetrics : LargeMetrics;

        Width = m.WindowWidth;
        Height = m.WindowHeight;

        CardBorder.Margin = m.CardMargin;
        CardBorder.Padding = m.CardPadding;
        CardBorder.CornerRadius = m.CardCorner;

        Orb.Width = m.OrbSize;
        Orb.Height = m.OrbSize;
        Orb.Margin = m.OrbMargin;
        StatusIcon.FontSize = m.IconSize;

        LanguageChip.Margin = m.ChipMargin;
        LanguageChip.Padding = m.ChipPadding;
        LanguageChip.MinWidth = m.ChipMinWidth;
        LanguageChipText.FontSize = m.ChipFontSize;

        Waveform.Height = m.WaveformHeight;
        StatusLabel.FontSize = m.StatusFontSize;
        StatusLabel.Margin = m.StatusMargin;
        PreviewLabel.FontSize = m.PreviewFontSize;

        CloseButton.Width = m.CloseSize;
        CloseButton.Height = m.CloseSize;
        CloseButton.Margin = m.CloseMargin;

        UpdateTextLines();
        PositionBottomCenter(); // the window just changed size; re-center it on its new one
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Dispatcher.Invoke(ApplyWidgetSize);

    /// <summary>
    /// Decides which of the two text lines is on screen. The large widget shows both — the status
    /// and, under it, the words as they are recognized. The compact one has room for a single
    /// line, and the transcript is the more useful of the two while there is one.
    /// </summary>
    private void UpdateTextLines()
    {
        var hasPreview = !string.IsNullOrEmpty(PreviewText);
        PreviewVisibility = hasPreview ? Visibility.Visible : Visibility.Collapsed;
        StatusVisibility = _compact == true && hasPreview ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Keeps the most recent <paramref name="n"/> characters — the words just spoken.</summary>
    private static string TailOf(string s, int n) => s.Length <= n ? s : "…" + s[^n..];

    /// <summary>
    /// State colors for the orb. Deliberately the softened versions of the semantic palette
    /// used across the app rather than raw Crimson/Gold/LimeGreen — the widget floats over
    /// other people's windows, and saturated primaries read as an error dialog.
    /// </summary>
    private static readonly Brush IdleBrush = Frozen("#B9C2D0");
    private static readonly Brush ListeningBrush = Frozen("#FF6B7E");
    private static readonly Brush ProcessingBrush = Frozen("#FFC24B");
    private static readonly Brush DoneBrush = Frozen("#3FD68C");

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze(); // shared across threads and never mutated
        return brush;
    }

    private void SetStatus(DictationState state, string? detail = null)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText = detail ?? state switch
            {
                DictationState.Listening => "Listening…",
                DictationState.Processing => "Transcribing…",
                DictationState.Done => "Done",
                _ => "Press the hotkey to dictate",
            };
            (StatusIcon.Symbol, StatusBrush) = state switch
            {
                DictationState.Listening => (SymbolRegular.Mic24, ListeningBrush),
                DictationState.Processing => (SymbolRegular.Sparkle24, ProcessingBrush),
                DictationState.Done => (SymbolRegular.CheckmarkCircle24, DoneBrush),
                _ => (SymbolRegular.Mic24, IdleBrush),
            };

            // Drives the pulse ring in XAML: only "Listening" animates.
            State = state.ToString();
        });
    }

    // ---- Bindable view-state properties ----
    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(DictationWidget),
            new PropertyMetadata("Press the hotkey to dictate"));
    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    /// <summary>
    /// The current <see cref="DictationState"/> as a string, bound by the widget's XAML to
    /// decide which visual state (notably the listening pulse) is running.
    /// </summary>
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(string), typeof(DictationWidget),
            new PropertyMetadata(nameof(DictationState.Idle)));
    public string State
    {
        get => (string)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public static readonly DependencyProperty StatusBrushProperty =
        DependencyProperty.Register(nameof(StatusBrush), typeof(Brush), typeof(DictationWidget),
            new PropertyMetadata(Brushes.White));
    public Brush StatusBrush
    {
        get => (Brush)GetValue(StatusBrushProperty);
        set => SetValue(StatusBrushProperty, value);
    }

    /// <summary>The 2-letter lowercase code shown on the chip ("--" until something is detected).</summary>
    public static readonly DependencyProperty DetectedLanguageProperty =
        DependencyProperty.Register(nameof(DetectedLanguage), typeof(string), typeof(DictationWidget),
            new PropertyMetadata("--"));
    public string DetectedLanguage
    {
        get => (string)GetValue(DetectedLanguageProperty);
        set => SetValue(DetectedLanguageProperty, value);
    }

    /// <summary>Dimmed until a language is detected, so the chip reads as "not decided yet".</summary>
    public static readonly DependencyProperty LanguageChipBrushProperty =
        DependencyProperty.Register(nameof(LanguageChipBrush), typeof(Brush), typeof(DictationWidget),
            new PropertyMetadata(Brushes.WhiteSmoke));
    public Brush LanguageChipBrush
    {
        get => (Brush)GetValue(LanguageChipBrushProperty);
        set => SetValue(LanguageChipBrushProperty, value);
    }

    /// <summary>Accent ring drawn once the user has corrected the language by hand.</summary>
    public static readonly DependencyProperty LanguageChipBorderProperty =
        DependencyProperty.Register(nameof(LanguageChipBorder), typeof(Brush), typeof(DictationWidget),
            new PropertyMetadata(Brushes.Transparent));
    public Brush LanguageChipBorder
    {
        get => (Brush)GetValue(LanguageChipBorderProperty);
        set => SetValue(LanguageChipBorderProperty, value);
    }

    /// <summary>The chip only appears when language routing is actually running.</summary>
    public static readonly DependencyProperty LanguageChipVisibilityProperty =
        DependencyProperty.Register(nameof(LanguageChipVisibility), typeof(Visibility), typeof(DictationWidget),
            new PropertyMetadata(Visibility.Collapsed));
    public Visibility LanguageChipVisibility
    {
        get => (Visibility)GetValue(LanguageChipVisibilityProperty);
        set => SetValue(LanguageChipVisibilityProperty, value);
    }

    /// <summary>Text recognized so far, shown live while the user is still speaking.</summary>
    public static readonly DependencyProperty PreviewTextProperty =
        DependencyProperty.Register(nameof(PreviewText), typeof(string), typeof(DictationWidget),
            new PropertyMetadata("", OnPreviewTextChanged));
    public string PreviewText
    {
        get => (string)GetValue(PreviewTextProperty);
        set => SetValue(PreviewTextProperty, value);
    }

    /// <summary>Collapsed when there is no live text, so the pill keeps its compact look.</summary>
    public static readonly DependencyProperty PreviewVisibilityProperty =
        DependencyProperty.Register(nameof(PreviewVisibility), typeof(Visibility), typeof(DictationWidget),
            new PropertyMetadata(Visibility.Collapsed));
    public Visibility PreviewVisibility
    {
        get => (Visibility)GetValue(PreviewVisibilityProperty);
        set => SetValue(PreviewVisibilityProperty, value);
    }

    /// <summary>Whether the status line is on screen; the compact widget hides it for the
    /// transcript, which is the more useful of the two when only one fits.</summary>
    public static readonly DependencyProperty StatusVisibilityProperty =
        DependencyProperty.Register(nameof(StatusVisibility), typeof(Visibility), typeof(DictationWidget),
            new PropertyMetadata(Visibility.Visible));
    public Visibility StatusVisibility
    {
        get => (Visibility)GetValue(StatusVisibilityProperty);
        set => SetValue(StatusVisibilityProperty, value);
    }

    private static void OnPreviewTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DictationWidget widget) return;
        widget.UpdateTextLines();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _settings.Changed -= OnSettingsChanged;
        CleanupCapture();
        _session?.Cancel();
        DisposeSession(_session);
        base.OnClosing(e);
    }
}
