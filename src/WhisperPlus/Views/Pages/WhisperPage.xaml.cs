using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.Input;
using WhisperPlus.Helpers;
using WhisperPlus.Models;
using WhisperPlus.Services;

namespace WhisperPlus.Views.Pages;

/// <summary>
/// Voice-model management for every engine: picks the active one (Whisper, Vosk or Nemotron), lists
/// each catalog with its download status, and lets the user download/delete models and choose
/// the active one per engine. Each installed model's card also binds an optional global
/// <c>preset</c> shortcut — assigning one creates a <see cref="DictationPreset"/> so that
/// hotkey starts dictation with that exact engine+model. Downloads run on a background task
/// with progress reported to the page's progress bar.
/// </summary>
public partial class WhisperPage : UserControl, INotifyPropertyChanged, IRefreshablePage
{
    private readonly ISettingsService _settings;
    private readonly WhisperModelManager _models;
    private readonly VoskModelManager _voskModels;
    private readonly NemotronModelManager _nemotronModels;
    private readonly BarkModelManager _barkModels;
    private readonly HotkeyService _hotkeys;
    private CancellationTokenSource? _downloadCts;
    private string _storageSummary = "";
    private bool _loading;

    /// <summary>Guards the single shared progress bar against two concurrent downloads.</summary>
    private bool _downloading;

    /// <summary>The button currently capturing a new hotkey, or null when idle.</summary>
    private ButtonBase? _capturingButton;

    public ObservableCollection<SpeechModelRow> Rows { get; } = new();
    public ObservableCollection<SpeechModelRow> VoskRows { get; } = new();
    public ObservableCollection<SpeechModelRow> NemotronRows { get; } = new();
    public ObservableCollection<SpeechModelRow> BarkRows { get; } = new();

    public string StorageSummary
    {
        get => _storageSummary;
        private set { if (_storageSummary != value) { _storageSummary = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public WhisperPage(
        ISettingsService settings,
        WhisperModelManager models,
        VoskModelManager voskModels,
        NemotronModelManager nemotronModels,
        BarkModelManager barkModels,
        HotkeyService hotkeys)
    {
        _settings = settings;
        _models = models;
        _voskModels = voskModels;
        _nemotronModels = nemotronModels;
        _barkModels = barkModels;
        _hotkeys = hotkeys;
        InitializeComponent();
        DataContext = this;
        ModelFolderLabel.Text = _models.ModelsDirectory;
        Refresh();
    }

    /// <summary>Rebuilds every model list from the catalogs + disk state.</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            EngineCombo.SelectedIndex = SpeechEngines.Normalize(_settings.Current.SpeechEngine) switch
            {
                SpeechEngines.Vosk => 1,
                SpeechEngines.Nemotron => 2,
                SpeechEngines.Bark => 3,
                _ => 0,
            };
        }
        finally { _loading = false; }

        Rows.Clear();
        long whisperTotal = 0;
        var activeWhisper = _settings.Current.ActiveWhisperModel;
        foreach (var info in WhisperModelCatalog.Models)
        {
            var installed = _models.IsInstalled(info.Id);
            var sizeMb = installed ? _models.GetInstalledSizeMb(info.Id) : 0;
            whisperTotal += sizeMb;
            var hotkey = DictationPresets.FindFor(_settings.Current.DictationPresets, SpeechEngines.Whisper, info.Id)?.Hotkey;
            Rows.Add(new SpeechModelRow(SpeechEngines.Whisper, info.Id, info.DisplayName, info.SizeMb,
                installed, info.Id == activeWhisper, sizeMb, hotkey, this));
        }

        VoskRows.Clear();
        long voskTotal = 0;
        var activeVosk = _settings.Current.ActiveVoskModel;
        foreach (var info in VoskModelCatalog.Models)
        {
            var installed = _voskModels.IsInstalled(info.Id);
            var sizeMb = installed ? _voskModels.GetInstalledSizeMb(info.Id) : 0;
            voskTotal += sizeMb;
            var hotkey = DictationPresets.FindFor(_settings.Current.DictationPresets, SpeechEngines.Vosk, info.Id)?.Hotkey;
            VoskRows.Add(new SpeechModelRow(SpeechEngines.Vosk, info.Id, info.DisplayName, info.SizeMb,
                installed, info.Id == activeVosk, sizeMb, hotkey, this));
        }

        NemotronRows.Clear();
        long nemotronTotal = 0;
        var activeNemotron = _settings.Current.ActiveNemotronModel;
        foreach (var info in NemotronModelCatalog.Models)
        {
            var installed = _nemotronModels.IsInstalled(info.Id);
            var sizeMb = installed ? _nemotronModels.GetInstalledSizeMb(info.Id) : 0;
            nemotronTotal += sizeMb;
            var hotkey = DictationPresets.FindFor(_settings.Current.DictationPresets, SpeechEngines.Nemotron, info.Id)?.Hotkey;
            NemotronRows.Add(new SpeechModelRow(SpeechEngines.Nemotron, info.Id, info.DisplayName, info.SizeMb,
                installed, info.Id == activeNemotron, sizeMb, hotkey, this));
        }

        BarkRows.Clear();
        long barkTotal = 0;
        var activeBark = _settings.Current.ActiveBarkModel;
        foreach (var info in BarkModelCatalog.Models)
        {
            var installed = _barkModels.IsInstalled(info.Id);
            var sizeMb = installed ? _barkModels.GetInstalledSizeMb(info.Id) : 0;
            barkTotal += sizeMb;
            var hotkey = DictationPresets.FindFor(_settings.Current.DictationPresets, SpeechEngines.Bark, info.Id)?.Hotkey;
            BarkRows.Add(new SpeechModelRow(SpeechEngines.Bark, info.Id, info.DisplayName, info.SizeMb,
                installed, info.Id == activeBark, sizeMb, hotkey, this));
        }

        StorageSummary =
            $"{_models.ModelsDirectory}  •  Whisper {whisperTotal} MB  •  Vosk {voskTotal} MB  •  " +
            $"Nemotron {nemotronTotal} MB  •  Bark {barkTotal} MB on disk";
        ModelList.ItemsSource = Rows;
        VoskModelList.ItemsSource = VoskRows;
        NemotronModelList.ItemsSource = NemotronRows;
        BarkModelList.ItemsSource = BarkRows;
        ModelFolderLabel.Text = _models.ModelsDirectory;
    }

    private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var engine = EngineCombo.SelectedIndex switch
        {
            1 => SpeechEngines.Vosk,
            2 => SpeechEngines.Nemotron,
            3 => SpeechEngines.Bark,
            _ => SpeechEngines.Whisper,
        };
        _settings.Update(s => s.SpeechEngine = engine);
    }

    /// <summary>Downloads the model on a background task, updating the progress bar live.</summary>
    public async Task DownloadAsync(string engine, string id)
    {
        if (_downloading)
        {
            DownloadStatus.Text = "Another download is already running.";
            return;
        }

        _downloading = true;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadStatus.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;

        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<double>(v => Dispatcher.Invoke(() =>
        {
            DownloadProgress.Value = v;
            DownloadStatus.Text = $"Downloading {id}… {v:0}%";
        }));

        try
        {
            // Vosk, Nemotron and Bark are multi-phase (unpack / four separate files), so they get a
            // status channel on top of the percentage; Whisper is a single file and needs none.
            var status = new Progress<string>(text => Dispatcher.Invoke(() => DownloadStatus.Text = text));
            if (engine == SpeechEngines.Vosk)
            {
                await _voskModels.DownloadAsync(id, progress, status, _downloadCts.Token);
            }
            else if (engine == SpeechEngines.Nemotron)
            {
                await _nemotronModels.DownloadAsync(id, progress, status, _downloadCts.Token);
            }
            else if (engine == SpeechEngines.Bark)
            {
                await _barkModels.DownloadAsync(id, progress, status, _downloadCts.Token);
            }
            else
            {
                await _models.DownloadAsync(id, progress, _downloadCts.Token);
            }
            DownloadStatus.Text = $"{id} downloaded.";
        }
        catch (OperationCanceledException)
        {
            DownloadStatus.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            _downloading = false;
            await Task.Delay(1500);
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadStatus.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    /// <summary>Sets the active model for an engine in settings and refreshes the rows.</summary>
    public void SetActive(string engine, string id)
    {
        switch (engine)
        {
            case SpeechEngines.Vosk: _settings.Update(s => s.ActiveVoskModel = id); break;
            case SpeechEngines.Nemotron: _settings.Update(s => s.ActiveNemotronModel = id); break;
            case SpeechEngines.Bark: _settings.Update(s => s.ActiveBarkModel = id); break;
            default: _settings.Update(s => s.ActiveWhisperModel = id); break;
        }
        Refresh();
    }

    /// <summary>Deletes a model and refreshes.</summary>
    public void Delete(string engine, string id)
    {
        switch (engine)
        {
            case SpeechEngines.Vosk: _voskModels.Delete(id); break;
            case SpeechEngines.Nemotron: _nemotronModels.Delete(id); break;
            case SpeechEngines.Bark: _barkModels.Delete(id); break;
            default: _models.Delete(id); break;
        }
        Refresh();
    }

    private void BrowseModelFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the voice-model library folder",
            InitialDirectory = _models.ModelsDirectory,
        };
        if (dialog.ShowDialog() != true) return;

        _settings.Update(s => s.WhisperModelsDirectory = dialog.FolderName);
        Refresh();
    }

    // ---- Per-model preset hotkey capture (reuses the HotkeysPage capture pattern) ----

    /// <summary>The (action, property) of every built-in hotkey, for collision checks.</summary>
    private static readonly (string Action, System.Reflection.PropertyInfo Prop)[] s_builtinHotkeys =
    {
        (HotkeyActions.Dictate,        typeof(AppSettings).GetProperty(nameof(AppSettings.HotkeyDictate))!),
        (HotkeyActions.FixLayout,      typeof(AppSettings).GetProperty(nameof(AppSettings.HotkeyFixLayout))!),
        (HotkeyActions.RecorderStart,  typeof(AppSettings).GetProperty(nameof(AppSettings.HotkeyRecorderStart))!),
        (HotkeyActions.RecorderPause,  typeof(AppSettings).GetProperty(nameof(AppSettings.HotkeyRecorderPause))!),
        (HotkeyActions.RecorderStop,   typeof(AppSettings).GetProperty(nameof(AppSettings.HotkeyRecorderStop))!),
        (HotkeyActions.CopyLastTranscript, typeof(AppSettings).GetProperty(nameof(AppSettings.HotkeyCopyLastTranscript))!),
        ("Screenshot",                 typeof(AppSettings).GetProperty(nameof(AppSettings.HotkeyScreenshot))!),
    };

    /// <summary>True if <paramref name="gesture"/> is already claimed by a built-in or a different preset.</summary>
    private bool IsGestureInUse(string gesture, string? exceptEngine, string? exceptModel)
    {
        var s = _settings.Current;
        foreach (var (_, prop) in s_builtinHotkeys)
        {
            if (string.Equals((string?)prop.GetValue(s), gesture, StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var p in s.DictationPresets)
        {
            if (p.Engine == exceptEngine && p.Model == exceptModel) continue; // editing itself is fine
            if (string.Equals(p.Hotkey, gesture, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Bound to each card's "Set shortcut" / hotkey button. Enters capture mode.</summary>
    public void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button) return;
        if (button.DataContext is not SpeechModelRow row) return;
        if (!row._installed) return; // only installed models can bind a shortcut

        // Clicking the same button again cancels an in-progress capture.
        if (ReferenceEquals(_capturingButton, button))
        {
            CancelCapture();
            return;
        }
        if (_capturingButton is not null) CancelCapture(); // finish any prior capture first

        _capturingButton = button;
        row.HotkeyLabel = "Press keys…  (Esc to cancel)";
        button.PreviewKeyDown += Capture_PreviewKeyDown;
        button.Focus();
    }

    private void Capture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (sender is not ButtonBase button || _capturingButton is null) return;
        if (button.DataContext is not SpeechModelRow row) return;

        if (e.Key == Key.Escape) { CancelCapture(); return; }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            row.HotkeyLabel = "Add a key, e.g. Ctrl+Shift+P";
            return;
        }

        var wpfKey = e.Key == Key.System ? e.SystemKey : e.Key;
        var displayKey = KeyToString(wpfKey);
        if (displayKey is null) { CancelCapture(); return; }

        var sb = new System.Text.StringBuilder();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) sb.Append("Ctrl+");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) sb.Append("Alt+");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) sb.Append("Shift+");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) sb.Append("Win+");
        sb.Append(displayKey);

        var gesture = sb.ToString();
        if (HotkeyGesture.Parse(gesture) is null)
        {
            row.HotkeyLabel = "Invalid — try again";
            return;
        }

        // Keep capturing when the gesture was refused: the label then carries the reason, and the
        // next keypress replaces it instead of leaving the user to click the button again.
        if (!AssignHotkey(row, gesture, button)) return;
        EndCapture(button);
    }

    /// <summary>
    /// Creates/updates the preset for <paramref name="row"/> and registers it globally. Returns false
    /// when the gesture was refused, leaving the capture session to the caller.
    /// </summary>
    private bool AssignHotkey(SpeechModelRow row, string gesture, ButtonBase button)
    {
        // Reject collisions against built-ins and *other* presets before touching the registry,
        // so a denied assignment leaves the existing binding intact.
        if (IsGestureInUse(gesture, row.Engine, row.Id))
        {
            row.HotkeyLabel = "In use — try another";
            return false;
        }

        var existing = DictationPresets.FindFor(_settings.Current.DictationPresets, row.Engine, row.Id);
        if (existing is not null)
        {
            // Re-register: drop the old global binding, register the new gesture on the same id.
            _hotkeys.Unregister(HotkeyActions.PresetAction(existing.Id));
            if (!_hotkeys.Register(HotkeyActions.PresetAction(existing.Id), gesture))
            {
                _hotkeys.Register(HotkeyActions.PresetAction(existing.Id), existing.Hotkey); // restore
                row.HotkeyLabel = "Unavailable - try another";
                return false;
            }
            _settings.Update(s =>
            {
                var p = DictationPresets.FindFor(s.DictationPresets, row.Engine, row.Id);
                if (p is not null)
                {
                    var idx = s.DictationPresets.IndexOf(p);
                    s.DictationPresets[idx] = p with { Hotkey = gesture };
                }
            });
        }
        else
        {
            var id = Guid.NewGuid().ToString("N");
            if (!_hotkeys.Register(HotkeyActions.PresetAction(id), gesture))
            {
                row.HotkeyLabel = "Unavailable - try another";
                return false;
            }
            _settings.Update(s =>
            {
                // Re-check under the lock: another assignment could have raced (unlikely in UI, but safe).
                if (DictationPresets.FindFor(s.DictationPresets, row.Engine, row.Id) is null)
                    s.DictationPresets.Add(new DictationPreset(id, row.Engine, row.Id, gesture));
            });
        }
        Refresh();
        return true;
    }

    /// <summary>Bound to each card's clear (✕) button. Removes the preset and unregisters it.</summary>
    public void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button || button.DataContext is not SpeechModelRow row) return;
        var existing = DictationPresets.FindFor(_settings.Current.DictationPresets, row.Engine, row.Id);
        if (existing is null) return;

        _hotkeys.Unregister(HotkeyActions.PresetAction(existing.Id));
        _settings.Update(s =>
        {
            var p = DictationPresets.FindFor(s.DictationPresets, row.Engine, row.Id);
            if (p is not null) s.DictationPresets.Remove(p);
        });
        Refresh();
    }

    private void CancelCapture()
    {
        if (_capturingButton is { } button)
        {
            if (button.DataContext is SpeechModelRow row)
            {
                var existing = DictationPresets.FindFor(_settings.Current.DictationPresets, row.Engine, row.Id)?.Hotkey;
                row.HotkeyLabel = existing ?? "Set shortcut";
            }
            EndCapture(button);
        }
    }

    private void EndCapture(ButtonBase button)
    {
        button.PreviewKeyDown -= Capture_PreviewKeyDown;
        _capturingButton = null;
    }

    /// <summary>Converts a WPF <see cref="Key"/> to the gesture token used by <see cref="HotkeyGesture"/>.</summary>
    private static string? KeyToString(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Oem3 => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.Oem5 => "\\",
        Key.Oem1 => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        _ => null,
    };
}

/// <summary>
/// One row in a model list, for either engine. Owns its command buttons and reports status
/// text + the visibility of each button based on its installed/active state. Also carries the
/// optional global preset shortcut bound to this model, with a live-editable label so the
/// capture flow can show "Press keys…" / "Set shortcut" / the gesture without rebuilding rows.
/// </summary>
public sealed class SpeechModelRow : INotifyPropertyChanged
{
    private readonly WhisperPage _page;
    internal readonly bool _installed;   // page guards shortcut capture to installed models only
    private readonly bool _isActive;
    private readonly long _sizeMb;
    private readonly int _downloadMb;
    private string _hotkeyLabel;

    public SpeechModelRow(
        string engine,
        string id,
        string displayName,
        int downloadMb,
        bool installed,
        bool active,
        long sizeMb,
        string? hotkey,
        WhisperPage page)
    {
        _page = page;
        Engine = engine;
        Id = id;
        DisplayName = displayName;
        _downloadMb = downloadMb;
        _installed = installed;
        _isActive = active;
        _sizeMb = sizeMb;
        _hotkeyLabel = string.IsNullOrEmpty(hotkey) ? "Set shortcut" : hotkey;
        HasHotkey = !string.IsNullOrEmpty(hotkey);

        DownloadCommand = new AsyncRelayCommand(() => _page.DownloadAsync(Engine, Id));
        DeleteCommand = new RelayCommand(() => _page.Delete(Engine, Id));
        UseCommand = new RelayCommand(() => _page.SetActive(Engine, Id));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    /// <summary>Which engine this model belongs to (see <see cref="SpeechEngines"/>).</summary>
    public string Engine { get; }
    public string Id { get; }
    public string DisplayName { get; }

    public string StatusText => _installed
        ? $"{_downloadMb} MB  •  {_sizeMb} MB on disk"
        : $"~{_downloadMb} MB to download";

    /// <summary>True when this is the model its engine will actually dictate with.</summary>
    public bool IsActive => _isActive;

    /// <summary>Per-engine glyph shown on the row's icon tile.</summary>
    public Wpf.Ui.Controls.SymbolRegular Icon => Engine switch
    {
        SpeechEngines.Vosk => Wpf.Ui.Controls.SymbolRegular.Flash24,
        SpeechEngines.Nemotron => Wpf.Ui.Controls.SymbolRegular.BrainCircuit24,
        SpeechEngines.Bark => Wpf.Ui.Controls.SymbolRegular.SoundWaveCircle24,
        _ => Wpf.Ui.Controls.SymbolRegular.Mic24,
    };

    public Visibility DownloadVisible => _installed ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DeleteVisible => _installed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UseVisible => (_installed && !_isActive) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActiveVisible => _isActive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The shortcut control is only meaningful for installed, ready-to-dictate models.</summary>
    public Visibility HotkeyVisible => _installed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Label shown on the card's shortcut button — the bound gesture, or "Set shortcut".</summary>
    public string HotkeyLabel
    {
        get => _hotkeyLabel;
        set { if (_hotkeyLabel != value) { _hotkeyLabel = value; OnPropertyChanged(); } }
    }

    /// <summary>True when a preset shortcut is bound, so the clear (✕) button can show.</summary>
    public bool HasHotkey { get; private set; }

    public IAsyncRelayCommand DownloadCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand UseCommand { get; }
}
