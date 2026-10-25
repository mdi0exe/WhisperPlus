using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WhisperPlus.Services;
using WhisperPlus.Views;

namespace WhisperPlus;

/// <summary>
/// Application entry point. Boots a lightweight Generic Host with Serilog logging
/// and a DI container that lives for the whole process lifetime, then starts the
/// first window. The host is kept as a static so WPF's Application lifecycle can
/// shut it down cleanly on exit.
/// </summary>
public partial class App : Application
{
    private static IHost? _host;

    /// <summary>Global, scoped root DI service provider for the running app.</summary>
    public static IServiceProvider Services => (_host ?? throw new InvalidOperationException("Host not started.")).Services;

    /// <summary>Convenience accessor for the settings service singleton.</summary>
    public static ISettingsService Settings => Services.GetRequiredService<ISettingsService>();

    /// <summary>Per-user roaming app-data folder (created on demand).</summary>
    public static string AppDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WhisperPlus");

    /// <summary>Default folder where speech models are stored (GGML files, plus a
    /// <c>vosk</c> subfolder for unpacked Vosk models and a <c>nemotron</c> one for the
    /// sherpa-onnx bundles).</summary>
    public static string DefaultModelsDirectory => Path.Combine(AppDataDirectory, "models");

    /// <summary>Folder where recordings are stored by default.</summary>
    public static string DefaultRecordingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WhisperPlus Recordings");

    private SingleInstanceService? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // One instance per session: a second launch owns no tray icon, no hotkeys and no keyboard
        // hook of its own — its command line (typically --capture from a launcher) goes to the
        // running instance over the single-instance pipe and this process exits.
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.TryAcquire())
        {
            SingleInstanceService.TryForward(e.Args);
            Shutdown();
            return;
        }

        // Make sure per-user directories exist before anything tries to read/write them.
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(DefaultModelsDirectory);
        Directory.CreateDirectory(DefaultRecordingsDirectory);

        ConfigureLogging();
        RegisterGlobalExceptionHandlers();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(ConfigureServices)
            .Build();

        // The app lives in the tray, so its lifetime cannot be tied to open windows: a
        // startup launch shows none at all, and hiding the settings window must not quit.
        // MainWindow triggers a real shutdown itself when "minimize to tray" is off.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        await _host.StartAsync();
        Log.Information("WhisperPlus started. AppData: {AppData}", AppDataDirectory);

        StartupPostHost(StartupService.WasLaunchedMinimized(e.Args), StartupService.WasLaunchedForCapture(e.Args));

        base.OnStartup(e);
    }

    /// <summary>
    /// Logs what would otherwise die silently: dispatcher exceptions and unobserved task faults.
    ///
    /// <para>Several hotkey paths are fire-and-forget (`_ = ...`), so a fault in them used to vanish
    /// until the process crashed somewhere unrelated. Unobserved task exceptions are marked observed
    /// after logging — on .NET they no longer take the process down by default, and a background
    /// helper should not either.</para>
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception.");
            args.Handled = true; // log it and keep the tray app alive; crashing serves nobody
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Fatal(ex, "Unhandled exception outside the dispatcher (IsTerminating={Terminating}).",
                    args.IsTerminating);
        };
    }

    /// <summary>
    /// After the host is up, eagerly resolve and wire the long-lived UI-thread services:
    /// the tray icon, hotkeys, and startup-sync. These live for the whole process and are
    /// started here rather than via IHostedService to keep them on the WPF UI thread.
    /// </summary>
    /// <param name="startMinimized">
    /// True when Windows launched us from the Run entry (<c>--minimized</c>): everything is
    /// wired up and the tray icon appears, but the settings window is left unshown.
    /// </param>
    private void StartupPostHost(bool startMinimized, bool captureOnStart = false)
    {
        var settings = Settings;

        // Tray icon.
        var tray = Services.GetRequiredService<TrayService>();
        tray.Initialize();
        tray.ShowSettingsRequested += () => Dispatcher.Invoke(BringSettingsToFront);
        tray.ShowDictationRequested += () => Dispatcher.Invoke(() => ShowDictationWidget());
        tray.ShowRecorderRequested += () => Dispatcher.Invoke(() => ShowRecorderWidget());
        tray.ScreenshotRequested += () => Dispatcher.Invoke(() => _ = Services.GetRequiredService<ScreenCapture.ScreenshotService>().CaptureAsync());
        tray.PinClipboardRequested += () => Dispatcher.Invoke(() => ScreenCapture.PinnedWindow.FromClipboard()?.Show());
        tray.ExitRequested += () => Dispatcher.Invoke(ShutdownApp);

        // Hotkeys.
        var hotkeys = Services.GetRequiredService<HotkeyService>();
        hotkeys.Initialize();
        RegisterAllHotkeys(hotkeys, settings.Current);
        hotkeys.HotkeyPressed += OnHotkeyPressed;
        hotkeys.HotkeyReleased += OnHotkeyReleased;

        // Keep the registry Run entry in sync with the user's setting.
        var startup = Services.GetRequiredService<StartupService>();
        startup.SetEnabled(settings.Current.LaunchOnStartup);

        // Screenshot capture. Installing the low-level hook is the only startup cost; the
        // SkiaSharp drawing stack and the overlay are created on the first capture.
        var screenshots = Services.GetRequiredService<ScreenCapture.ScreenshotService>();
        screenshots.BeforeCapture = HideOwnWidgets;
        screenshots.AfterCapture = RestoreOwnWidgets;
        screenshots.Initialize();

        // A second launch of the exe forwards its command line here instead of starting up:
        // --capture opens the overlay, --dictate / --record open their widgets, anything else shows settings.
        _singleInstance!.CommandReceived += cmd => Dispatcher.Invoke(() =>
        {
            if (cmd.Contains("capture", StringComparison.OrdinalIgnoreCase))
                _ = screenshots.CaptureAsync();
            else if (cmd.Contains("dictate", StringComparison.OrdinalIgnoreCase))
                ShowDictationWidget();
            else if (cmd.Contains("record", StringComparison.OrdinalIgnoreCase))
                ShowRecorderWidget();
            else if (cmd.Contains("pin-clipboard", StringComparison.OrdinalIgnoreCase))
                ScreenCapture.PinnedWindow.FromClipboard()?.Show();
            else
                BringSettingsToFront();
        });

        // --capture: open the overlay as soon as the message loop is idle, without waiting for
        // the global shortcut. The process still lives in the tray afterwards.
        if (captureOnStart)
            Dispatcher.BeginInvoke(new Action(() => _ = screenshots.CaptureAsync()),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Apply the saved theme, color (accent + background wash), and backdrop before the
        // first window appears.
        var theme = Services.GetRequiredService<ThemeService>();
        theme.ApplyAll(
            settings.Current.Theme,
            settings.Current.AccentColor,
            settings.Current.EnableBackdrop);

        // A startup launch stops here: the tray icon and hotkeys are live, and the window is
        // created only when the user actually asks for it (tray click / "Open Settings").
        if (startMinimized)
        {
            Log.Information("Launched with {Switch}; staying in the tray.", StartupService.MinimizedSwitch);
            return;
        }

        // Show the main window (resolved via DI so it gets its services injected).
        Services.GetRequiredService<MainWindow>().Show();
    }

    private static void RegisterAllHotkeys(HotkeyService hotkeys, Models.AppSettings s)
    {
        hotkeys.Register(HotkeyActions.Dictate, s.HotkeyDictate);
        hotkeys.Register(HotkeyActions.FixLayout, s.HotkeyFixLayout);
        hotkeys.Register(HotkeyActions.RecorderStart, s.HotkeyRecorderStart);
        hotkeys.Register(HotkeyActions.RecorderPause, s.HotkeyRecorderPause);
        hotkeys.Register(HotkeyActions.RecorderStop, s.HotkeyRecorderStop);
        hotkeys.Register(HotkeyActions.CopyLastTranscript, s.HotkeyCopyLastTranscript);

        // Per-engine/model preset shortcuts. Each is registered under preset:<id> so the
        // handler can look the preset back up and start dictation with its engine+model.
        foreach (var preset in s.DictationPresets)
        {
            if (string.IsNullOrWhiteSpace(preset.Hotkey)) continue;
            hotkeys.Register(HotkeyActions.PresetAction(preset.Id), preset.Hotkey);
        }
    }

    private void OnHotkeyPressed(string action)
    {
        // A preset shortcut starts a dictation session with that preset's engine+model,
        // ignoring the active-engine/model settings for that one session.
        if (HotkeyActions.TryGetPresetId(action, out var presetId))
        {
            var preset = Models.DictationPresets.FindById(Settings.Current.DictationPresets, presetId);
            if (preset is null) return;
            var widget = EnsureDictationVisible();
            if (Settings.Current.DictationHoldToTalk)
            {
                widget.StartWithPreset(preset.Engine, preset.Model);
                Services.GetRequiredService<HotkeyService>().TrackRelease(action);
            }
            else
            {
                // Toggle-style presets: if a session is already running, stop it (same UX as
                // the default dictate hotkey); otherwise start one with the preset.
                if (widget.IsSessionActive) widget.StopDictation();
                else widget.StartWithPreset(preset.Engine, preset.Model);
            }
            return;
        }

        switch (action)
        {
            case HotkeyActions.Dictate:
                if (Settings.Current.DictationHoldToTalk)
                {
                    EnsureDictationVisible().StartDictation();
                    Services.GetRequiredService<HotkeyService>().TrackRelease(HotkeyActions.Dictate);
                }
                else
                {
                    EnsureDictationVisible().Toggle();
                }
                break;
            case HotkeyActions.FixLayout:
                _ = Services.GetRequiredService<LayoutFixService>().FixAsync();
                break;
            case HotkeyActions.RecorderStart:
                EnsureRecorderVisible().CommandStart();
                break;
            case HotkeyActions.RecorderPause:
                // Only a recorder that is already on screen can be paused. Resolving the widget from
                // the container would construct it (and enumerate audio devices) just to pause
                // nothing, and would do so behind the user's back.
                _recorderWidget?.CommandPause();
                break;
            case HotkeyActions.RecorderStop:
                _recorderWidget?.CommandStop();
                break;
            case HotkeyActions.CopyLastTranscript:
                // Fire-and-forget: the copy marshals itself to this thread and logs its own
                // failures, so the hotkey pump must not block waiting on the clipboard.
                _ = Services.GetRequiredService<TranscriptHistory>().CopyLastAsync();
                break;
        }
    }

    private void OnHotkeyReleased(string action)
    {
        // Both the default dictate hotkey and preset shortcuts stop on release in hold-to-talk.
        if (action == HotkeyActions.Dictate && Settings.Current.DictationHoldToTalk)
            EnsureDictationVisible().StopDictation();
        else if (HotkeyActions.TryGetPresetId(action, out _) && Settings.Current.DictationHoldToTalk)
            EnsureDictationVisible().StopDictation();
    }

    private void BringSettingsToFront()
    {
        // Always the DI singleton: after a startup launch the window does not exist yet, and
        // resolving it here creates exactly one instance rather than a second copy.
        var window = Services.GetRequiredService<MainWindow>();
        window.BringToForeground();
    }

    /// <summary>Shows the floating recorder widget (creating it on first use).</summary>
    private void ShowRecorderWidget() => EnsureRecorderVisible();

    /// <summary>Resolves the recorder widget singleton and ensures it is visible.</summary>
    private Views.Widgets.RecorderWidget EnsureRecorderVisible()
    {
        var widget = Services.GetRequiredService<Views.Widgets.RecorderWidget>();
        _recorderWidget = widget;
        if (!widget.IsVisible) widget.Show();
        return widget;
    }

    /// <summary>Shows the floating dictation widget (creating it on first use).</summary>
    private void ShowDictationWidget() => EnsureDictationVisible();

    /// <summary>Resolves the dictation widget singleton and ensures it is visible.</summary>
    private Views.Widgets.DictationWidget EnsureDictationVisible()
    {
        var widget = Services.GetRequiredService<Views.Widgets.DictationWidget>();
        _dictationWidget = widget;
        if (!widget.IsVisible) widget.Show();
        return widget;
    }

    // Widgets are tracked here rather than resolved on demand: asking the container for one
    // would create it, and a screenshot must not be the reason a widget starts existing.
    private Views.Widgets.DictationWidget? _dictationWidget;
    private Views.Widgets.RecorderWidget? _recorderWidget;
    private bool _dictationHiddenForCapture;
    private bool _recorderHiddenForCapture;

    /// <summary>Hides the floating widgets so they do not appear inside a screenshot.</summary>
    private void HideOwnWidgets()
    {
        _dictationHiddenForCapture = _dictationWidget?.IsVisible == true;
        _recorderHiddenForCapture = _recorderWidget?.IsVisible == true;
        if (_dictationHiddenForCapture) _dictationWidget!.Hide();
        if (_recorderHiddenForCapture) _recorderWidget!.Hide();
    }

    /// <summary>Restores only the widgets the capture hid.</summary>
    private void RestoreOwnWidgets()
    {
        if (_dictationHiddenForCapture) _dictationWidget?.Show();
        if (_recorderHiddenForCapture) _recorderWidget?.Show();
        _dictationHiddenForCapture = false;
        _recorderHiddenForCapture = false;
    }

    private void ShutdownApp()
    {
        Views.MainWindow.ApplicationIsExiting = true;
        // Pins hold frozen bitmaps of the screen; leaving one open would keep the process alive
        // with a stray window on the desktop.
        ScreenCapture.PinnedWindow.CloseAll();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("WhisperPlus exiting...");

        // Synchronous by design: an async OnExit returns at the first await and the process can exit
        // before the host stops or the log is flushed. Five seconds is the ceiling for the host's
        // own graceful stop, after which we let it go.
        try { _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warning(ex, "The host did not stop cleanly."); }
        _host?.Dispose();

        _singleInstance?.Dispose();

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// Registers every application service in the DI container. Services are
    /// singletons where they own native/Win32 resources (tray, hotkeys, settings),
    /// and transient where they are short-lived per-call helpers.
    /// </summary>
    private static void ConfigureServices(IServiceCollection services)
    {
        // Core / settings
        services.AddSingleton<ISettingsService, SettingsService>();

        // Background platform services
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<StartupService>();

        // Feature 3: layout fix (text capture + conversion + injection)
        services.AddSingleton<TextSelectionService>();
        services.AddSingleton<TextInjector>();
        services.AddSingleton<LayoutFixService>();

        // Clipboard access sits behind an interface so the transcript history can be tested
        // without a desktop; the WPF implementation marshals every call to the STA thread.
        services.AddSingleton<IClipboardAccess, WpfClipboardAccess>();
        services.AddSingleton<TranscriptHistory>();

        // Feature 2: audio recorder + device enumeration + floating widget
        services.AddSingleton<AudioDeviceService>();
        services.AddSingleton<AudioRecorder>();
        services.AddSingleton<Views.Widgets.RecorderWidget>();

        // Feature 4: screenshot & annotation. Nothing is loaded until the first capture, so
        // this registration does not change the app's idle memory footprint.
        services.AddSingleton<ScreenCapture.ScreenshotService>();

        // Feature 1: offline speech-to-text — Whisper (accurate), Vosk (lightweight), Nemotron
        // (accurate *and* streaming, at the cost of a much larger download) and Bark (Parakeet-TDT
        // transducers, offline but multilingual across the 25 European languages)
        services.AddSingleton<WhisperModelManager>();
        services.AddSingleton<WhisperService>();
        services.AddSingleton<VoskModelManager>();
        services.AddSingleton<VoskService>();
        services.AddSingleton<NemotronModelManager>();
        services.AddSingleton<NemotronService>();
        services.AddSingleton<BarkModelManager>();
        services.AddSingleton<BarkService>();

        // Language identification + the routing that picks the engine/model for a language.
        // Both backends are registered; LanguageDetector picks one per session and falls back to
        // the other when the configured model has not been downloaded.
        services.AddSingleton<LanguageIdModelManager>();
        services.AddSingleton<SileroLanguageIdentifier>();
        services.AddSingleton<WhisperLanguageIdentifier>();
        services.AddSingleton<LanguageDetector>();
        services.AddSingleton<LanguageRouter>();

        services.AddSingleton<Views.Widgets.DictationWidget>();

        // Theming
        services.AddSingleton<ThemeService>();

        // Windows (resolved on demand)
        services.AddSingleton<MainWindow>();
    }

    private static void ConfigureLogging()
    {
        var logPath = Path.Combine(AppDataDirectory, "logs", "whisperplus-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
    }
}
