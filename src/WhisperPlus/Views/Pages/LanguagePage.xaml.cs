using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;
using WhisperPlus.Models;
using WhisperPlus.Services;
using Wpf.Ui.Controls;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
// Both this file's System.Windows.Controls.TextBlock (constructed below) and Wpf.Ui.Controls.TextBlock
// (referenced via the XAML) are in scope; resolve the unqualified name to the standard WPF control.
using TextBlock = System.Windows.Controls.TextBlock;

namespace WhisperPlus.Views.Pages;

/// <summary>
/// Real-time language routing settings: which languages the user dictates in, which model handles
/// each one, and which small Whisper model does the identifying. Every control persists
/// immediately through <see cref="ISettingsService"/>, matching the other settings pages.
/// </summary>
public partial class LanguagePage : UserControl, IRefreshablePage
{
    private readonly ISettingsService _settings;
    private readonly WhisperModelManager _whisperModels;
    private readonly VoskModelManager _voskModels;
    private readonly NemotronModelManager _nemotronModels;
    private readonly LanguageIdModelManager _langIdModels;
    private bool _loading;

    /// <summary>Cancels an in-flight Silero download when the user hits Cancel.</summary>
    private CancellationTokenSource? _sileroDownloadCts;

    /// <summary>The language chips, kept so selection can be redrawn without rebuilding them.</summary>
    private readonly List<LanguageChip> _chips = new();

    public ObservableCollection<LanguageRouteRow> Routes { get; } = new();

    public LanguagePage(
        ISettingsService settings,
        WhisperModelManager whisperModels,
        VoskModelManager voskModels,
        NemotronModelManager nemotronModels,
        LanguageIdModelManager langIdModels)
    {
        _settings = settings;
        _whisperModels = whisperModels;
        _voskModels = voskModels;
        _nemotronModels = nemotronModels;
        _langIdModels = langIdModels;
        InitializeComponent();
        DataContext = this;

        BuildLanguageChips();
        Refresh();
    }

    /// <summary>Rebuilds every control from the current settings and what is installed on disk.</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;
            RoutingToggle.IsChecked = s.LanguageRoutingEnabled;
            DeferDetectionToggle.IsChecked = s.DetectLanguageAfterSpeech;
            LiveToggle.IsChecked = s.LiveTranscription;
            UpdateDeferDetectionState();
            BuildManualLanguageCombo(s);
            BuildBackendCombo(s.LanguageDetectorBackend);
            BuildDetectorCombo(s.LanguageDetectorModel);
            RefreshSileroCard();
            HighlightChips(s.ActiveLanguages);
            BuildRoutes(s);
            UpdateDetectorWarning();
        }
        finally { _loading = false; }
    }

    // ---- Toggles ----

    private void RoutingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s.LanguageRoutingEnabled = RoutingToggle.IsChecked == true);
        UpdateManualLanguageState();
        UpdateDeferDetectionState();
    }

    private void DeferDetectionToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s.DetectLanguageAfterSpeech = DeferDetectionToggle.IsChecked == true);
        UpdateDeferDetectionState();
    }

    /// <summary>
    /// Spells out which of the two the user has chosen — and what it costs them — rather than leaving a
    /// bare toggle whose consequence (an accurate answer they wait for, or a fast one that can be
    /// wrong) is invisible. Dimmed while routing is off, for the same reason the manual picker is
    /// dimmed while it is on: there is no detection to schedule then.
    /// </summary>
    private void UpdateDeferDetectionState()
    {
        var routing = RoutingToggle.IsChecked == true;
        DeferDetectionToggle.IsEnabled = routing;

        DeferDetectionCard.Description = !routing
            ? "Only applies with real-time routing on — with it off, nothing is detecting the language " +
              "to begin with."
            : DeferDetectionToggle.IsChecked == true
                ? "On: the language is decided when you press the shortcut again, from everything you " +
                  "said instead of the first second or two of it. More accurate, and it never switches " +
                  "models mid-dictation — but the model for your language can only start loading once " +
                  "you stop, so a large one is waited on, and nothing appears while you speak."
                : "Off: the language is identified as you talk and its model loads in the background, so " +
                  "the text is ready the moment you stop. Judged on the opening of the sentence, which " +
                  "is where a wrong guess (and a mid-dictation model switch) comes from.";
    }

    // ---- Manual dictation language (used when routing is off) ----

    /// <summary>
    /// Fills the picker with "Automatic" plus the active languages. Only active languages are
    /// offered so this list and the widget's quick-switch menu can never disagree — a code the chip
    /// cannot switch back to would be a one-way trip.
    /// </summary>
    private void BuildManualLanguageCombo(AppSettings s)
    {
        var options = new List<DetectorOption> { new("", "Automatic  ·  let the model decide") };
        foreach (var code in s.ActiveLanguages)
            options.Add(new DetectorOption(code, $"{code}  ·  {LanguageCatalog.DisplayName(code)}"));

        ManualLanguageCombo.ItemsSource = options;
        ManualLanguageCombo.SelectedIndex = 0;
        foreach (var option in options)
        {
            if (option.Id != s.DictationLanguage) continue;
            ManualLanguageCombo.SelectedItem = option;
            break;
        }

        UpdateManualLanguageState();
    }

    /// <summary>
    /// Dims the picker while routing is on: detection is choosing the language then, and a control
    /// that silently does nothing is worse than one that says it is not in charge right now.
    /// </summary>
    private void UpdateManualLanguageState()
    {
        var routing = RoutingToggle.IsChecked == true;
        ManualLanguageCombo.IsEnabled = !routing;
        ManualLanguageCard.Description = routing
            ? "Real-time routing is on, so the language is detected while you speak and this is not used. " +
              "Turn routing off to choose the language yourself."
            : "Used when real-time routing is off: your engine and model stay as they are, but they are told " +
              "which language they are hearing instead of guessing per sentence. The widget's chip can still " +
              "change it mid-dictation.";
    }

    private void ManualLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ManualLanguageCombo.SelectedItem is not DetectorOption option) return;

        _settings.Update(s => s.DictationLanguage = option.Id);
    }

    private void LiveToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Update(s => s.LiveTranscription = LiveToggle.IsChecked == true);
    }

    // ---- Detector backend ----

    private void BuildBackendCombo(string selected)
    {
        var options = new List<DetectorOption>
        {
            new(LanguageDetectorBackends.Silero,  "Silero language classifier  ·  recommended"),
            new(LanguageDetectorBackends.Whisper, "Whisper detector model"),
        };

        BackendCombo.ItemsSource = options;
        var normalized = LanguageDetectorBackends.Normalize(selected);
        foreach (var option in options)
        {
            if (option.Id != normalized) continue;
            BackendCombo.SelectedItem = option;
            return;
        }
        BackendCombo.SelectedIndex = 0;
    }

    private void BackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (BackendCombo.SelectedItem is not DetectorOption option) return;

        _settings.Update(s => s.LanguageDetectorBackend = option.Id);
        UpdateDetectorWarning();
    }

    // ---- Silero model download ----

    private void RefreshSileroCard()
    {
        var installed = _langIdModels.IsInstalled;

        SileroCard.Description = installed
            ? $"Installed · {_langIdModels.InstalledSizeMb} MB. Trained to identify 95 languages, so it does not " +
              "assume English the way Whisper's detector does."
            : $"Not downloaded · about {LanguageIdModelCatalog.SizeMb} MB. Until it is, language detection falls " +
              "back to the Whisper model below.";

        SileroDownloadButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        SileroDeleteButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SileroDownload_Click(object sender, RoutedEventArgs e)
    {
        // A second click while a download is running cancels it, matching the button's label.
        if (_sileroDownloadCts is not null)
        {
            _sileroDownloadCts.Cancel();
            return;
        }

        using var cts = new CancellationTokenSource();
        _sileroDownloadCts = cts;

        SileroDownloadButton.Content = "Cancel";
        SileroProgress.Visibility = Visibility.Visible;
        SileroProgress.Value = 0;

        var progress = new Progress<double>(pct => SileroProgress.Value = pct);
        try
        {
            SileroCard.Description = "Downloading…";
            await _langIdModels.DownloadAsync(progress, cts.Token);
            SileroCard.Description = "Downloaded.";
        }
        catch (OperationCanceledException)
        {
            SileroCard.Description = "Download cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Silero language-ID model download failed.");
            SileroCard.Description = $"Download failed: {ex.Message}";
        }
        finally
        {
            _sileroDownloadCts = null;
            SileroDownloadButton.Content = "Download";
            SileroProgress.Visibility = Visibility.Collapsed;
            RefreshSileroCard();
            UpdateDetectorWarning();
        }
    }

    private void SileroDelete_Click(object sender, RoutedEventArgs e)
    {
        _langIdModels.Delete();
        RefreshSileroCard();
        UpdateDetectorWarning();
    }

    // ---- Detector model ----

    /// <summary>
    /// Only multilingual Whisper models can identify a language, so the English-only ones are
    /// left out entirely rather than offered and then rejected.
    /// </summary>
    private void BuildDetectorCombo(string selected)
    {
        var options = new List<DetectorOption>();
        foreach (var model in WhisperModelCatalog.Models)
        {
            if (model.Id.EndsWith(".en", StringComparison.OrdinalIgnoreCase)) continue;
            var installed = _whisperModels.IsInstalled(model.Id);
            options.Add(new DetectorOption(
                model.Id,
                installed ? $"{model.DisplayName}" : $"{model.DisplayName}  ·  not downloaded"));
        }

        DetectorCombo.ItemsSource = options;
        foreach (var option in options)
        {
            if (option.Id != selected) continue;
            DetectorCombo.SelectedItem = option;
            return;
        }
        if (options.Count > 0) DetectorCombo.SelectedIndex = 0;
    }

    private void DetectorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (DetectorCombo.SelectedItem is not DetectorOption option) return;

        _settings.Update(s => s.LanguageDetectorModel = option.Id);
        UpdateDetectorWarning();
    }

    /// <summary>
    /// Warns only when *neither* backend can identify a language, since either one alone is enough:
    /// the detector falls back to whichever model is actually on disk.
    /// </summary>
    private void UpdateDetectorWarning()
    {
        var s = _settings.Current;
        var sileroReady = _langIdModels.IsInstalled;
        var whisperReady = _whisperModels.IsInstalled(s.LanguageDetectorModel);

        if (sileroReady || whisperReady)
        {
            DetectorWarning.IsOpen = false;
            DetectorWarning.Message = "";
            return;
        }

        DetectorWarning.IsOpen = true;
        DetectorWarning.Message =
            $"Neither the Silero model nor the Whisper '{s.LanguageDetectorModel}' model is downloaded, so the " +
            "language cannot be identified and dictation will keep using your default engine. Download the Silero " +
            "model above, or the Whisper one on the Voice models page.";
    }

    // ---- Active languages ----

    /// <summary>One clickable language chip: a rounded pill with a check when active.</summary>
    private sealed record LanguageChip(string Code, Border Pill, SymbolIcon Check, TextBlock Label);

    private void BuildLanguageChips()
    {
        LanguagePanel.Children.Clear();
        _chips.Clear();

        foreach (var language in LanguageCatalog.Languages)
        {
            var check = new SymbolIcon
            {
                Symbol = SymbolRegular.Checkmark24,
                FontSize = 13,
                Margin = new Thickness(0, 0, 6, 0),
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
            };
            check.SetResourceReference(SymbolIcon.ForegroundProperty, "Wp.AccentText");

            var label = new TextBlock
            {
                Text = $"{language.Code} · {language.NativeName}",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(check);
            content.Children.Add(label);

            var pill = new Border
            {
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 8, 8),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = language.Code,
                ToolTip = language.EnglishName,
                Child = content,
            };

            // Tunneling mouse-down, marked handled — the click belongs to the chip and nothing
            // above it (the same reason the color swatches on GeneralPage do it this way).
            pill.PreviewMouseLeftButtonDown += LanguageChip_Click;

            LanguagePanel.Children.Add(pill);
            _chips.Add(new LanguageChip(language.Code, pill, check, label));
        }
    }

    private void LanguageChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border pill || pill.Tag is not string code) return;
        e.Handled = true;

        _settings.Update(s =>
        {
            if (!s.ActiveLanguages.Remove(code)) s.ActiveLanguages.Add(code);

            // A language that is no longer active must not keep a mapping behind it, or the
            // sanitizer would silently drop it on the next load anyway.
            s.LanguageRoutes.RemoveAll(r => !s.ActiveLanguages.Contains(r.Code));
        });

        Refresh();
    }

    private void HighlightChips(IReadOnlyList<string> active)
    {
        foreach (var chip in _chips)
        {
            var selected = active.Contains(chip.Code);
            chip.Check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;

            if (selected)
            {
                chip.Pill.SetResourceReference(Border.BorderBrushProperty, "Wp.AccentEdge");
                chip.Pill.SetResourceReference(Border.BackgroundProperty, "Wp.AccentFaint");
                chip.Label.SetResourceReference(TextBlock.ForegroundProperty, "Wp.TextPrimary");
            }
            else
            {
                chip.Pill.SetResourceReference(Border.BorderBrushProperty, "Wp.Stroke");
                chip.Pill.SetResourceReference(Border.BackgroundProperty, "Wp.Surface");
                chip.Label.SetResourceReference(TextBlock.ForegroundProperty, "Wp.TextSecondary");
            }
        }
    }

    // ---- Mapping table ----

    private void BuildRoutes(AppSettings s)
    {
        Routes.Clear();
        foreach (var code in s.ActiveLanguages)
        {
            var route = LanguageRoutes.FindFor(s.LanguageRoutes, code);
            Routes.Add(new LanguageRouteRow(code, route, this));
        }

        RouteList.ItemsSource = Routes;
        EmptyRoutesInfo.IsOpen = Routes.Count == 0;
    }

    /// <summary>The installed models a language can be mapped to on a given engine.</summary>
    internal List<RouteModelOption> ModelsFor(string engine, string code)
    {
        var options = new List<RouteModelOption>();

        if (engine == SpeechEngines.Vosk)
        {
            // Vosk models are single-language, so only the ones for this language make sense.
            foreach (var model in VoskModelCatalog.Models)
            {
                if (model.Language != code) continue;
                if (!_voskModels.IsInstalled(model.Id)) continue;
                options.Add(new RouteModelOption(model.Id, model.DisplayName));
            }
            return options;
        }

        if (engine == SpeechEngines.Nemotron)
        {
            // One multilingual model, but only for the ~35 languages it was trained on — Persian
            // among others is absent, and offering it there would route fa at fluent nonsense.
            if (!NemotronModelCatalog.SupportsLanguage(code)) return options;

            foreach (var model in NemotronModelCatalog.Models)
            {
                if (!_nemotronModels.IsInstalled(model.Id)) continue;
                options.Add(new RouteModelOption(model.Id, model.DisplayName));
            }
            return options;
        }

        foreach (var model in WhisperModelCatalog.Models)
        {
            // An English-only model can serve English and nothing else.
            var englishOnly = model.Id.EndsWith(".en", StringComparison.OrdinalIgnoreCase);
            if (englishOnly && code != "en") continue;
            if (!_whisperModels.IsInstalled(model.Id)) continue;
            options.Add(new RouteModelOption(model.Id, model.DisplayName));
        }
        return options;
    }

    /// <summary>Persists one row's engine/model choice. Called by the row when the user changes it.</summary>
    internal void SaveRoute(string code, string engine, string? model)
    {
        _settings.Update(s =>
        {
            s.LanguageRoutes.RemoveAll(r => r.Code == code);
            if (!string.IsNullOrWhiteSpace(model))
                s.LanguageRoutes.Add(new LanguageRoute(code, engine, model));
        });
    }

    /// <summary>An entry in the detector-model dropdown.</summary>
    internal sealed record DetectorOption(string Id, string DisplayName);
}

/// <summary>An installed model a language can be routed to.</summary>
public sealed record RouteModelOption(string Id, string DisplayName);

/// <summary>
/// One row of the mapping table: a language, the engine chosen for it, and the installed model on
/// that engine. Changing either dropdown persists the route immediately; switching engine reloads
/// the model list, since the two catalogs share nothing.
/// </summary>
public sealed class LanguageRouteRow : INotifyPropertyChanged
{
    private readonly LanguagePage _page;
    private readonly string _code;
    private string _selectedEngine;
    private RouteModelOption? _selectedModel;
    private bool _loading = true;

    public LanguageRouteRow(string code, LanguageRoute? route, LanguagePage page)
    {
        _page = page;
        _code = code;
        DisplayName = $"{code}  ·  {LanguageCatalog.DisplayName(code)}";
        _selectedEngine = EngineLabel(route?.Engine ?? SpeechEngines.Whisper);

        Models = new ObservableCollection<RouteModelOption>(_page.ModelsFor(EngineId(_selectedEngine), code));
        if (route is not null)
        {
            foreach (var model in Models)
            {
                if (model.Id != route.Model) continue;
                _selectedModel = model;
                break;
            }
        }
        _loading = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string DisplayName { get; }

    public IReadOnlyList<string> Engines { get; } = new[] { "Whisper", "Vosk", "Nemotron" };

    public ObservableCollection<RouteModelOption> Models { get; }

    public string StatusText => Models.Count == 0
        // An empty Nemotron list usually means the language simply isn't one of the ~35 it was
        // trained on, which no amount of downloading would fix — say so rather than implying it.
        ? EngineId(_selectedEngine) == SpeechEngines.Nemotron && !NemotronModelCatalog.SupportsLanguage(_code)
            ? "Nemotron does not support this language"
            : "No installed model for this language on this engine"
        : _selectedModel is null
            ? "Not mapped — pick a model"
            : $"Routed to {_selectedModel.DisplayName}";

    public string SelectedEngine
    {
        get => _selectedEngine;
        set
        {
            if (_selectedEngine == value || string.IsNullOrEmpty(value)) return;
            _selectedEngine = value;
            OnPropertyChanged();

            // The model list belongs to the engine, so it is rebuilt and the old choice dropped.
            Models.Clear();
            foreach (var model in _page.ModelsFor(EngineId(value), _code)) Models.Add(model);
            _selectedModel = null;
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(StatusText));

            Save();
        }
    }

    public RouteModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (ReferenceEquals(_selectedModel, value)) return;
            _selectedModel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            Save();
        }
    }

    private void Save()
    {
        if (_loading) return;
        _page.SaveRoute(_code, EngineId(_selectedEngine), _selectedModel?.Id);
    }

    private static string EngineLabel(string engineId) => engineId switch
    {
        SpeechEngines.Vosk => "Vosk",
        SpeechEngines.Nemotron => "Nemotron",
        _ => "Whisper",
    };

    private static string EngineId(string label) => label switch
    {
        "Vosk" => SpeechEngines.Vosk,
        "Nemotron" => SpeechEngines.Nemotron,
        _ => SpeechEngines.Whisper,
    };
}
