using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Serilog;
using WhisperPlus.Helpers;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>Contract for the persistent settings store.</summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler? Changed;
    void Save();
    void Reload();
    /// <summary>Applies a mutator to the in-memory snapshot, then persists and notifies.</summary>
    void Update(Action<AppSettings> mutator);
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as pretty/indented JSON to
/// %APPDATA%\WhisperPlus\settings.json. Writes are atomic (write-to-temp then rename)
/// so a crash mid-write can never corrupt the settings file. Exposes a
/// <see cref="Changed"/> event so other services/UI can react to live edits.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsService()
    {
        _filePath = Path.Combine(App.AppDataDirectory, "settings.json");
        _current = LoadWithDefaults();
    }

    /// <summary>The current in-memory settings snapshot. Mutate and call <see cref="Save"/>.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    public event EventHandler? Changed;

    /// <summary>Atomically persists the current settings and raises <see cref="Changed"/>.</summary>
    public void Save()
    {
        AppSettings snapshot;
        lock (_gate) snapshot = _current;

        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(snapshot, s_jsonOptions);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_filePath)) File.Replace(tmp, _filePath, destinationBackupFileName: null);
            else File.Move(tmp, _filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings to {Path}", _filePath);
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reload()
    {
        lock (_gate) _current = LoadWithDefaults();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Loads from disk; if missing or invalid, falls back to defaults and applies
    /// runtime-computed defaults (e.g. recordings directory).</summary>
    private AppSettings LoadWithDefaults()
    {
        AppSettings settings;
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions) ?? AppSettings.CreateDefault();
            }
            else
            {
                settings = AppSettings.CreateDefault();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Settings file unreadable, using defaults.");
            settings = AppSettings.CreateDefault();
        }

        // Apply runtime-computed defaults for empty/missing path fields.
        if (string.IsNullOrWhiteSpace(settings.RecordingOutputDirectory))
            settings.RecordingOutputDirectory = App.DefaultRecordingsDirectory;
        if (string.IsNullOrWhiteSpace(settings.WhisperModelsDirectory))
            settings.WhisperModelsDirectory = App.DefaultModelsDirectory;

        Sanitize(settings);

        return settings;

        static void Sanitize(AppSettings s)
        {
            // A malformed stored color (e.g. "#fffff" — wrong digit count) must not freeze
            // the accent or the tint. Fall back to the default so the next save rewrites a
            // valid value to disk.
            if (!IsValidHexColor(s.AccentColor)) s.AccentColor = "#6759FF";
            s.SpeechEngine = SpeechEngines.Normalize(s.SpeechEngine);
            s.DictationWidgetSize = WidgetSizes.Normalize(s.DictationWidgetSize);

            // An older settings.json predates DictationPresets, so the field can deserialize
            // as null. Guard that, and drop any entry whose hotkey is empty or no longer parses
            // (a manually edited file) so it can't register a broken global shortcut at startup.
            s.DictationPresets ??= new();
            if (s.DictationPresets.Count > 0)
            {
                s.DictationPresets.RemoveAll(p =>
                    string.IsNullOrWhiteSpace(p.Id) ||
                    string.IsNullOrWhiteSpace(p.Engine) ||
                    string.IsNullOrWhiteSpace(p.Model) ||
                    HotkeyGesture.Parse(p.Hotkey) is null);
            }

            SanitizeLanguageRouting(s);
            SanitizeScreenshot(s);
            s.PersianLayout = PersianLayoutVariants.Normalize(s.PersianLayout);
        }

        static void SanitizeScreenshot(AppSettings s)
        {
            // A hand-edited or older file may carry an unknown format or an out-of-range
            // quality; both would otherwise reach the encoder.
            s.ScreenshotFormat = (s.ScreenshotFormat ?? "").Trim().ToLowerInvariant() switch
            {
                "jpg" or "jpeg" => "jpg",
                "webp" => "webp",
                _ => "png",
            };
            s.ScreenshotQuality = Math.Clamp(s.ScreenshotQuality, 1, 100);
            if (string.IsNullOrWhiteSpace(s.HotkeyScreenshot))
                s.HotkeyScreenshot = "Win+Shift+S";
        }

        static bool IsValidHexColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return false;
            try { ColorConverter.ConvertFromString(hex); return true; }
            catch { return false; }
        }
    }

    /// <summary>Updates the in-memory snapshot from a mutator callback, then saves.</summary>
    public void Update(Action<AppSettings> mutator)
    {
        lock (_gate) mutator(_current);
        Save();
    }

    /// <summary>
    /// Normalizes the real-time routing settings so the rest of the app can trust them: language
    /// codes are 2-letter lowercase, known, and unique; routes point at an active language and a
    /// real engine; and the detector model is one that can actually identify a language. Public
    /// and static so it can be exercised directly without touching %APPDATA%.
    /// </summary>
    public static void SanitizeLanguageRouting(AppSettings s)
    {
        s.ActiveLanguages ??= new();
        s.LanguageRoutes ??= new();

        // Rebuild the active list: normalize case/whitespace, drop anything not in the catalog,
        // and collapse duplicates while keeping the user's ordering.
        var active = new HashSet<string>(StringComparer.Ordinal);
        var languages = new List<string>(s.ActiveLanguages.Count);
        foreach (var raw in s.ActiveLanguages)
        {
            var code = LanguageCatalog.Normalize(raw);
            if (code.Length == 0 || !LanguageCatalog.IsKnown(code)) continue;
            if (active.Add(code)) languages.Add(code);
        }
        s.ActiveLanguages = languages;

        // A route only means something for a language actually being listened for, and one with
        // no model would send dictation to a model id of "".
        var routes = new List<LanguageRoute>(s.LanguageRoutes.Count);
        var routed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in s.LanguageRoutes)
        {
            var code = LanguageCatalog.Normalize(route.Code);
            if (code.Length == 0 || !active.Contains(code)) continue;
            if (string.IsNullOrWhiteSpace(route.Model)) continue;
            if (!routed.Add(code)) continue;
            routes.Add(route with { Code = code, Engine = SpeechEngines.Normalize(route.Engine) });
        }
        s.LanguageRoutes = routes;

        // Unknown/misspelled backend ids fall back to the default rather than disabling detection.
        s.LanguageDetectorBackend = LanguageDetectorBackends.Normalize(s.LanguageDetectorBackend);

        // The hand-picked dictation language has to be one the user actually listens for, or the
        // Language page would offer a chip that isn't there and the widget would show a code the
        // quick-switch menu cannot switch back to. Anything else means "auto".
        var manual = LanguageCatalog.Normalize(s.DictationLanguage);
        s.DictationLanguage = active.Contains(manual) ? manual : "";

        // An English-only model throws "Failed to auto-detect language" the moment it is asked to
        // identify one, so it can never serve as the detector. Still sanitized when the Silero
        // backend is selected, because it remains the fallback if that model isn't downloaded.
        var detector = s.LanguageDetectorModel?.Trim() ?? "";
        if (WhisperModelCatalog.Find(detector) is null ||
            detector.EndsWith(".en", StringComparison.OrdinalIgnoreCase))
        {
            detector = "tiny";
        }
        s.LanguageDetectorModel = detector;
    }
}
