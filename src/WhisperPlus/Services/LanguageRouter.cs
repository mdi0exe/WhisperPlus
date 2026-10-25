using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Where one dictation session's audio should go: an engine + model, and the language code that
/// chose them (null when nothing was detected and the user's defaults are in play).
/// </summary>
public readonly record struct RouteTarget(string Engine, string Model, string? LanguageCode)
{
    /// <summary>True if this target runs on Vosk rather than Whisper.</summary>
    public bool IsVosk => Engine == SpeechEngines.Vosk;

    /// <summary>True if this target runs on Nemotron rather than Whisper or Vosk.</summary>
    public bool IsNemotron => Engine == SpeechEngines.Nemotron;

    /// <summary>True if this target runs on the Bark (Parakeet-TDT) engine.</summary>
    public bool IsBark => Engine == SpeechEngines.Bark;

    /// <summary>True when both engine and model are set to something usable.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Engine) && !string.IsNullOrWhiteSpace(Model);

    /// <summary>Whether this target points at the same engine + model as <paramref name="other"/>.</summary>
    public bool SameModelAs(RouteTarget other) =>
        Engine == other.Engine && Model == other.Model;
}

/// <summary>
/// Turns a detected language code into the engine + model the user mapped to it, and preloads
/// that model. This is the piece that makes real-time routing useful: as soon as the language is
/// known — while the user is still talking — the mapped model starts loading in the background,
/// so by the time they stop it is already resident.
/// </summary>
public sealed class LanguageRouter
{
    private readonly ISettingsService _settings;
    private readonly WhisperService _whisper;
    private readonly VoskService _vosk;
    private readonly NemotronService _nemotron;
    private readonly BarkService _bark;
    private readonly WhisperModelManager _whisperModels;
    private readonly VoskModelManager _voskModels;
    private readonly NemotronModelManager _nemotronModels;
    private readonly BarkModelManager _barkModels;

    public LanguageRouter(
        ISettingsService settings,
        WhisperService whisper,
        VoskService vosk,
        NemotronService nemotron,
        BarkService bark,
        WhisperModelManager whisperModels,
        VoskModelManager voskModels,
        NemotronModelManager nemotronModels,
        BarkModelManager barkModels)
    {
        _settings = settings;
        _whisper = whisper;
        _vosk = vosk;
        _nemotron = nemotron;
        _bark = bark;
        _whisperModels = whisperModels;
        _voskModels = voskModels;
        _nemotronModels = nemotronModels;
        _barkModels = barkModels;
    }

    /// <summary>Whether the user turned real-time language routing on.</summary>
    public bool IsRoutingEnabled => _settings.Current.LanguageRoutingEnabled;

    /// <summary>
    /// The languages detection should choose between. Empty (or single-entry) lists make routing
    /// pointless, which callers use to skip detection altogether.
    /// </summary>
    public IReadOnlyList<string> ActiveLanguages => _settings.Current.ActiveLanguages;

    /// <summary>
    /// The language the user picked by hand for when routing is off, or null when they left it on
    /// automatic. Not consulted while routing is on, where detection decides.
    /// </summary>
    public string? ManualLanguage
    {
        get
        {
            var code = LanguageCatalog.Normalize(_settings.Current.DictationLanguage);
            return code.Length == 2 ? code : null;
        }
    }

    /// <summary>
    /// The engine + model to use when no language has been detected: the user's active engine and
    /// its active model, exactly as dictation behaved before routing existed.
    ///
    /// <para>With routing off it also carries <see cref="ManualLanguage"/>, so a user who has told
    /// the app which language they speak gets the engine pinned to it from the first word — that is
    /// the whole point of choosing one. With routing on the code is left empty for detection to
    /// fill in.</para>
    /// </summary>
    public RouteTarget DefaultTarget()
    {
        var s = _settings.Current;
        var engine = SpeechEngines.Normalize(s.SpeechEngine);
        var model = engine switch
        {
            SpeechEngines.Vosk => s.ActiveVoskModel,
            SpeechEngines.Nemotron => s.ActiveNemotronModel,
            SpeechEngines.Bark => s.ActiveBarkModel,
            _ => s.ActiveWhisperModel,
        };
        return new RouteTarget(engine, model, IsRoutingEnabled ? null : ManualLanguage);
    }

    /// <summary>
    /// Resolves a language code to its mapped engine + model, falling back to
    /// <see cref="DefaultTarget"/> when routing is off, the code is malformed, or the user hasn't
    /// mapped that language. The fallback keeps the code attached whenever it is known, so the
    /// widget can show what is being transcribed and the engine can be pinned to it, even where no
    /// separate model was mapped — with routing off that is every language, since choosing a model
    /// per language is precisely what routing does and the user has turned it off.
    /// </summary>
    public RouteTarget Resolve(string? languageCode)
    {
        var code = LanguageCatalog.Normalize(languageCode);
        var fallback = DefaultTarget();
        if (code.Length != 2) return fallback;
        if (!IsRoutingEnabled) return fallback with { LanguageCode = code };

        var route = LanguageRoutes.FindFor(_settings.Current.LanguageRoutes, code);
        if (route is null) return fallback with { LanguageCode = code };

        return new RouteTarget(SpeechEngines.Normalize(route.Engine), route.Model, code);
    }

    /// <summary>Whether the target's model is present on disk and can therefore be loaded.</summary>
    public bool IsInstalled(RouteTarget target)
    {
        if (!target.IsValid) return false;
        return target.Engine switch
        {
            SpeechEngines.Vosk => _voskModels.IsInstalled(target.Model),
            SpeechEngines.Nemotron => _nemotronModels.IsInstalled(target.Model),
            SpeechEngines.Bark => _barkModels.IsInstalled(target.Model),
            _ => _whisperModels.IsInstalled(target.Model),
        };
    }

    /// <summary>
    /// Starts (or joins) the background load of a target's model. Returns false if the model
    /// isn't installed or failed to load, so the session can keep using whatever it already has.
    /// </summary>
    public Task<bool> PreloadAsync(RouteTarget target, CancellationToken ct = default)
    {
        if (!target.IsValid) return Task.FromResult(false);
        if (!IsInstalled(target))
        {
            Log.Warning("Route target {Engine}/{Model} is not installed; not preloading.",
                target.Engine, target.Model);
            return Task.FromResult(false);
        }

        return target.Engine switch
        {
            SpeechEngines.Vosk => _vosk.EnsureLoadedAsync(target.Model, ct),
            SpeechEngines.Nemotron => _nemotron.EnsureLoadedAsync(target.Model, ct),
            SpeechEngines.Bark => _bark.EnsureLoadedAsync(target.Model, ct),
            _ => _whisper.EnsureLoadedAsync(target.Model, ct),
        };
    }
}
