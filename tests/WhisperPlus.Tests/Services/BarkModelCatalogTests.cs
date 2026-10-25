using System.Collections.Generic;
using WhisperPlus.Models;
using WhisperPlus.Services;
using WhisperPlus.Tests.Services.Fakes;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for the Bark engine's catalog and the language facts routing depends on. No native model
/// is loaded: everything here is pure metadata, which is exactly the part that decides whether a
/// language is allowed to be routed to this engine at all.
/// </summary>
public class BarkModelCatalogTests
{
    [Fact]
    public void Multilingual_Export_Supports_Its_Twenty_Five_Languages()
    {
        // Spot-check a spread: English, a Romance language, a Nordic one and a Cyrillic one.
        Assert.True(BarkModelCatalog.SupportsLanguage("parakeet-tdt-0.6b-v3", "en"));
        Assert.True(BarkModelCatalog.SupportsLanguage("parakeet-tdt-0.6b-v3", "fr"));
        Assert.True(BarkModelCatalog.SupportsLanguage("parakeet-tdt-0.6b-v3", "sv"));
        Assert.True(BarkModelCatalog.SupportsLanguage("parakeet-tdt-0.6b-v3", "uk"));
    }

    /// <summary>
    /// Persian is the app's headline use case and no Bark/Parakeet export speaks it. Routing it
    /// here would produce confident nonsense instead of text, so the catalog has to say no.
    /// </summary>
    [Fact]
    public void Multilingual_Export_Rejects_Persian()
    {
        Assert.False(BarkModelCatalog.SupportsLanguage("parakeet-tdt-0.6b-v3", "fa"));
        Assert.False(BarkModelCatalog.EngineSupportsLanguage("fa"));
    }

    [Fact]
    public void English_Only_Export_Rejects_Every_Other_Language()
    {
        Assert.True(BarkModelCatalog.SupportsLanguage("parakeet-tdt-0.6b-v2", "en"));
        Assert.False(BarkModelCatalog.SupportsLanguage("parakeet-tdt-0.6b-v2", "de"));
    }

    [Fact]
    public void Unknown_Model_Id_Supports_Nothing()
    {
        Assert.False(BarkModelCatalog.SupportsLanguage("no-such-model", "en"));
        Assert.Null(BarkModelCatalog.Find("no-such-model"));
    }

    /// <summary>
    /// The router must pick the Bark model the user activated, not Whisper's, when Bark is the
    /// active engine — the switch that gained a branch for this engine.
    /// </summary>
    [Fact]
    public void Default_Target_Reads_The_Active_Bark_Model()
    {
        var settings = new AppSettings
        {
            SpeechEngine = SpeechEngines.Bark,
            ActiveBarkModel = "parakeet-tdt-0.6b-v2",
            ActiveWhisperModel = "large-v3-turbo",
        };

        var router = NewRouter(settings);
        var target = router.DefaultTarget();

        Assert.Equal(SpeechEngines.Bark, target.Engine);
        Assert.Equal("parakeet-tdt-0.6b-v2", target.Model);
        Assert.True(target.IsBark);
        Assert.False(target.IsVosk);
        Assert.False(target.IsNemotron);
    }

    [Theory]
    [InlineData("Bark", SpeechEngines.Bark)]
    [InlineData("bark", SpeechEngines.Bark)]
    [InlineData(" BARK ", SpeechEngines.Bark)]
    [InlineData("nemotron", SpeechEngines.Nemotron)]
    [InlineData(null, SpeechEngines.Whisper)]
    [InlineData("nonsense", SpeechEngines.Whisper)]
    public void Engine_Ids_Normalize(string? stored, string expected)
    {
        Assert.Equal(expected, SpeechEngines.Normalize(stored));
    }

    /// <summary>Every catalog entry must resolve its own repository to a concrete download URL.</summary>
    [Fact]
    public void Every_Model_Exposes_A_Resolvable_Url_For_Each_File()
    {
        foreach (var model in BarkModelCatalog.Models)
        {
            foreach (var file in BarkModelCatalog.Files)
            {
                var url = BarkModelCatalog.UrlFor(model.Repository, file);
                Assert.StartsWith("https://huggingface.co/", url);
                Assert.EndsWith("/" + file, url);
            }
        }
    }

    private static LanguageRouter NewRouter(AppSettings settings)
    {
        var fakeSettings = new FakeSettingsService(settings);
        var whisperModels = new WhisperModelManager(fakeSettings);
        var voskModels = new VoskModelManager(fakeSettings);
        var nemotronModels = new NemotronModelManager(fakeSettings);
        var barkModels = new BarkModelManager(fakeSettings);

        return new LanguageRouter(
            fakeSettings,
            new WhisperService(whisperModels),
            new VoskService(voskModels),
            new NemotronService(nemotronModels),
            new BarkService(barkModels),
            whisperModels, voskModels, nemotronModels, barkModels);
    }
}
