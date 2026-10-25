using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WhisperPlus.Models;
using WhisperPlus.Services;
using WhisperPlus.Tests.Services.Fakes;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for <see cref="LanguageRouter"/>: the pure decision of which engine + model a detected
/// language maps to, and the fallback to the user's defaults. These don't load any native model
/// — <see cref="LanguageRouter.Resolve"/>, <see cref="LanguageRouter.DefaultTarget"/>, and the
/// enabled/candidate accessors only read settings, so the router is built on real (un-loaded)
/// engine services and model managers backed by a fake settings service.
/// </summary>
public class LanguageRouterTests
{
    /// <summary>
    /// Builds a router over a fake settings snapshot. The engine services and model managers are
    /// real but never load anything in these tests — only the settings-reading members are
    /// exercised, so no native Whisper/Vosk/sherpa-onnx library is ever touched.
    /// </summary>
    private static LanguageRouter NewRouter(AppSettings settings)
    {
        var fakeSettings = new FakeSettingsService(settings);
        var whisperModels = new WhisperModelManager(fakeSettings);
        var voskModels = new VoskModelManager(fakeSettings);
        var nemotronModels = new NemotronModelManager(fakeSettings);
        var barkModels = new BarkModelManager(fakeSettings);
        var whisper = new WhisperService(whisperModels);
        var vosk = new VoskService(voskModels);
        var nemotron = new NemotronService(nemotronModels);
        var bark = new BarkService(barkModels);
        return new LanguageRouter(
            fakeSettings, whisper, vosk, nemotron, bark,
            whisperModels, voskModels, nemotronModels, barkModels);
    }

    // ---- Resolve ----

    /// <summary>
    /// With routing off the mapping table is ignored — that is what the toggle means — but the code
    /// itself is kept: the user can still say which language they are speaking, and the engine has
    /// to be told, or it goes back to guessing per utterance.
    /// </summary>
    [Fact]
    public void Resolve_WhenRoutingOff_Keeps_The_Code_On_The_Default_Engine_And_Model()
    {
        var s = new AppSettings
        {
            LanguageRoutingEnabled = false,
            SpeechEngine = SpeechEngines.Whisper,
            ActiveWhisperModel = "large-v3-turbo",
            ActiveLanguages = new List<string> { "fa", "en" },
            LanguageRoutes = new List<LanguageRoute>
            {
                new("fa", SpeechEngines.Vosk, "vosk-model-fa"),
            },
        };
        var router = NewRouter(s);

        var target = router.Resolve("fa");

        // Routing is off, so the fa → Vosk route is ignored and the active Whisper model is used.
        Assert.Equal(SpeechEngines.Whisper, target.Engine);
        Assert.Equal("large-v3-turbo", target.Model);
        Assert.Equal("fa", target.LanguageCode);
    }

    // ---- Hand-picked language (routing off) ----

    /// <summary>
    /// The language chosen on the Language page rides along on the default target, so a session
    /// starts pinned to it rather than waiting for something to detect it.
    /// </summary>
    [Fact]
    public void DefaultTarget_Carries_The_Hand_Picked_Language_When_Routing_Is_Off()
    {
        var s = new AppSettings
        {
            LanguageRoutingEnabled = false,
            SpeechEngine = SpeechEngines.Whisper,
            ActiveWhisperModel = "small",
            ActiveLanguages = new List<string> { "fa", "en" },
            DictationLanguage = "fa",
        };
        var router = NewRouter(s);

        var target = router.DefaultTarget();

        Assert.Equal(SpeechEngines.Whisper, target.Engine);
        Assert.Equal("small", target.Model);
        Assert.Equal("fa", target.LanguageCode);
        Assert.Equal("fa", router.ManualLanguage);
    }

    /// <summary>
    /// With routing on, detection owns the language: a stale hand-picked code must not pre-empt it,
    /// or the first probe would have nothing left to decide.
    /// </summary>
    [Fact]
    public void DefaultTarget_Ignores_The_Hand_Picked_Language_When_Routing_Is_On()
    {
        var s = new AppSettings
        {
            LanguageRoutingEnabled = true,
            SpeechEngine = SpeechEngines.Whisper,
            ActiveWhisperModel = "small",
            ActiveLanguages = new List<string> { "fa", "en" },
            DictationLanguage = "fa",
        };
        var router = NewRouter(s);

        Assert.Null(router.DefaultTarget().LanguageCode);
    }

    /// <summary>
    /// "Automatic" is the empty string, and a code the user no longer listens for is dropped by the
    /// sanitizer — both mean the engine decides, and neither may leave a code behind.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("de")]   // not in ActiveLanguages
    [InlineData("xx")]   // not a real language
    public void ManualLanguage_Is_Null_Unless_It_Is_An_Active_Language(string configured)
    {
        var s = new AppSettings
        {
            LanguageRoutingEnabled = false,
            ActiveLanguages = new List<string> { "fa", "en" },
            DictationLanguage = configured,
        };
        var router = NewRouter(s);

        Assert.Null(router.ManualLanguage);
        Assert.Null(router.DefaultTarget().LanguageCode);
    }

    [Fact]
    public void Resolve_Mapped_Code_Returns_Mapped_Target()
    {
        var s = new AppSettings
        {
            LanguageRoutingEnabled = true,
            ActiveLanguages = new List<string> { "fa", "en" },
            LanguageRoutes = new List<LanguageRoute>
            {
                new("fa", SpeechEngines.Vosk, "vosk-model-small-fa-0.42"),
                new("en", SpeechEngines.Whisper, "large-v3-turbo"),
            },
        };
        var router = NewRouter(s);

        var fa = router.Resolve("fa");
        Assert.Equal(SpeechEngines.Vosk, fa.Engine);
        Assert.Equal("vosk-model-small-fa-0.42", fa.Model);
        Assert.Equal("fa", fa.LanguageCode);

        var en = router.Resolve("EN");
        Assert.Equal(SpeechEngines.Whisper, en.Engine);
        Assert.Equal("large-v3-turbo", en.Model);
        Assert.Equal("en", en.LanguageCode);
    }

    /// <summary>
    /// An active-but-unmapped language keeps the default engine/model (so dictation still works)
    /// but carries the detected code, so the widget can show what was heard even though nothing
    /// special was mapped to it.
    /// </summary>
    [Fact]
    public void Resolve_Active_But_Unmapped_Code_Falls_Back_Keeping_Code()
    {
        var s = new AppSettings
        {
            LanguageRoutingEnabled = true,
            SpeechEngine = SpeechEngines.Whisper,
            ActiveWhisperModel = "tiny",
            ActiveLanguages = new List<string> { "fa", "en", "de" },
            LanguageRoutes = new List<LanguageRoute>
            {
                new("fa", SpeechEngines.Vosk, "vosk-model-fa"),
                // de is active but has no route
            },
        };
        var router = NewRouter(s);

        var target = router.Resolve("de");

        Assert.Equal(SpeechEngines.Whisper, target.Engine);
        Assert.Equal("tiny", target.Model);
        Assert.Equal("de", target.LanguageCode);
    }

    /// <summary>
    /// A malformed or missing code (not 2 letters) can't be a language, so the router falls all the
    /// way back to the default target with no code attached.
    /// </summary>
    [Theory]
    [InlineData("xyz")]    // too long
    [InlineData("f")]      // too short
    [InlineData("")]       // empty
    [InlineData(null)]     // nothing detected
    public void Resolve_Malformed_Or_Missing_Code_Falls_Back_To_Default(string? code)
    {
        var s = new AppSettings
        {
            LanguageRoutingEnabled = true,
            SpeechEngine = SpeechEngines.Whisper,
            ActiveWhisperModel = "base",
            ActiveLanguages = new List<string> { "fa", "en" },
            LanguageRoutes = new List<LanguageRoute>
            {
                new("fa", SpeechEngines.Vosk, "vosk-model-fa"),
            },
        };
        var router = NewRouter(s);

        var target = router.Resolve(code);

        Assert.Equal(SpeechEngines.Whisper, target.Engine);
        Assert.Equal("base", target.Model);
        Assert.Null(target.LanguageCode);
    }

    /// <summary>
    /// A 2-letter code that simply isn't mapped still uses the default model (dictation must keep
    /// working) but keeps the code attached, so the widget can show what was heard. Detection only
    /// ever produces active-language codes, so in practice this is an unmapped active language.
    /// </summary>
    [Fact]
    public void Resolve_Unmapped_Two_Letter_Code_Keeps_Code_On_Default()
    {
        var s = new AppSettings
        {
            LanguageRoutingEnabled = true,
            SpeechEngine = SpeechEngines.Whisper,
            ActiveWhisperModel = "base",
            ActiveLanguages = new List<string> { "fa", "en" },
            LanguageRoutes = new List<LanguageRoute>
            {
                new("fa", SpeechEngines.Vosk, "vosk-model-fa"),
            },
        };
        var router = NewRouter(s);

        var target = router.Resolve("zz");

        Assert.Equal(SpeechEngines.Whisper, target.Engine);
        Assert.Equal("base", target.Model);
        Assert.Equal("zz", target.LanguageCode);
    }

    // ---- DefaultTarget / Vosk engine ----

    [Fact]
    public void DefaultTarget_Reflects_Active_Vosk_Engine_When_Set()
    {
        var s = new AppSettings
        {
            SpeechEngine = SpeechEngines.Vosk,
            ActiveVoskModel = "vosk-model-small-en-us-0.15",
        };
        var router = NewRouter(s);

        var target = router.DefaultTarget();

        Assert.Equal(SpeechEngines.Vosk, target.Engine);
        Assert.Equal("vosk-model-small-en-us-0.15", target.Model);
        Assert.Null(target.LanguageCode);
    }

    [Fact]
    public void DefaultTarget_Reflects_Active_Nemotron_Engine_When_Set()
    {
        var s = new AppSettings
        {
            SpeechEngine = SpeechEngines.Nemotron,
            ActiveNemotronModel = "nemotron-3.5-streaming-0.6b-560ms",
        };
        var router = NewRouter(s);

        var target = router.DefaultTarget();

        Assert.Equal(SpeechEngines.Nemotron, target.Engine);
        Assert.Equal("nemotron-3.5-streaming-0.6b-560ms", target.Model);
        Assert.True(target.IsNemotron);
        Assert.False(target.IsVosk);
        Assert.Null(target.LanguageCode);
    }

    /// <summary>
    /// A language mapped to Nemotron resolves to it like any other engine — the model id belongs
    /// to the Nemotron catalog rather than the Whisper or Vosk one.
    /// </summary>
    [Fact]
    public void Resolve_Maps_A_Language_To_Nemotron()
    {
        var s = new AppSettings
        {
            LanguageRoutingEnabled = true,
            SpeechEngine = SpeechEngines.Whisper,
            ActiveWhisperModel = "tiny",
            ActiveLanguages = new List<string> { "fa", "en" },
            LanguageRoutes = new List<LanguageRoute>
            {
                new("en", SpeechEngines.Nemotron, "nemotron-3.5-streaming-0.6b-160ms"),
            },
        };
        var router = NewRouter(s);

        var target = router.Resolve("en");

        Assert.Equal(SpeechEngines.Nemotron, target.Engine);
        Assert.Equal("nemotron-3.5-streaming-0.6b-160ms", target.Model);
        Assert.Equal("en", target.LanguageCode);
    }

    // ---- candidates / enabled ----

    [Fact]
    public void ActiveLanguages_And_Enabled_Reflect_Settings()
    {
        var s = new AppSettings
        {
            LanguageRoutingEnabled = true,
            ActiveLanguages = new List<string> { "fa", "en", "de" },
        };
        var router = NewRouter(s);

        Assert.True(router.IsRoutingEnabled);
        Assert.Equal(new[] { "fa", "en", "de" }, router.ActiveLanguages);
    }

    [Fact]
    public async Task Preload_Invalid_Target_Returns_False_Without_Loading()
    {
        // An empty model id is not a real target; the router must short-circuit to false rather
        // than hand a bogus model id to the (real, native) engine service.
        var router = NewRouter(new AppSettings());
        var target = new RouteTarget(SpeechEngines.Whisper, "", "fa");

        var ok = await router.PreloadAsync(target, CancellationToken.None);

        Assert.False(ok);
    }
}
