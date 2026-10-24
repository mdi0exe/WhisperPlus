using System.Collections.Generic;
using System.Text.Json;
using WhisperPlus.Models;
using WhisperPlus.Services;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for the language-routing settings model: the catalog/route helpers the UI and router
/// lean on, the JSON round-trip that lets an old settings.json migrate, and the sanitizer that
/// keeps the rest of the app from having to defend against malformed values.
/// </summary>
public class LanguageRoutingTests
{
    // ---- LanguageCatalog.Normalize ----

    [Theory]
    [InlineData("fa", "fa")]
    [InlineData("FA", "fa")]
    [InlineData("  en  ", "en")]
    [InlineData("De", "de")]
    public void Normalize_Lowercases_And_Trims(string input, string expected)
    {
        Assert.Equal(expected, LanguageCatalog.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("f")]       // too short
    [InlineData("fas")]     // too long
    [InlineData("f a")]     // not 2 letters after trim
    public void Normalize_Rejects_NonCodes(string? input)
    {
        Assert.Equal("", LanguageCatalog.Normalize(input));
    }

    [Fact]
    public void Catalog_Offers_Persian_And_English()
    {
        Assert.NotNull(LanguageCatalog.Find("fa"));
        Assert.NotNull(LanguageCatalog.Find("en"));
        Assert.Equal("Persian", LanguageCatalog.Find("fa")!.EnglishName);
        Assert.Equal("فارسی", LanguageCatalog.Find("fa")!.NativeName);
    }

    [Fact]
    public void IsKnown_True_For_Catalog_Codes()
    {
        Assert.True(LanguageCatalog.IsKnown("fa"));
        Assert.True(LanguageCatalog.IsKnown("en"));
        Assert.False(LanguageCatalog.IsKnown("zz"));   // plausible 2-letter code, not offered
        Assert.False(LanguageCatalog.IsKnown(null));
    }

    [Fact]
    public void DisplayName_Combines_Native_And_English()
    {
        // Persian has distinct native/English names → "native · english".
        Assert.Equal("فارسی · Persian", LanguageCatalog.DisplayName("fa"));
        // English is identical in both → just the english name.
        Assert.Equal("English", LanguageCatalog.DisplayName("en"));
    }

    [Fact]
    public void DisplayName_Falls_Back_To_Code_For_Unknown()
    {
        Assert.Equal("zz", LanguageCatalog.DisplayName("zz"));
        Assert.Equal("—", LanguageCatalog.DisplayName("nope"));
    }

    // ---- LanguageRoutes.FindFor ----

    [Fact]
    public void FindFor_Matches_On_Normalized_Code()
    {
        var routes = new List<LanguageRoute>
        {
            new("fa", SpeechEngines.Whisper, "large-v3-turbo"),
            new("en", SpeechEngines.Vosk, "vosk-model-small-en-us-0.15"),
        };

        // Lookup is case- and whitespace-insensitive via the same Normalize used everywhere else.
        var match = LanguageRoutes.FindFor(routes, "FA");
        Assert.NotNull(match);
        Assert.Equal("large-v3-turbo", match!.Model);

        Assert.NotNull(LanguageRoutes.FindFor(routes, "  en "));
        Assert.Null(LanguageRoutes.FindFor(routes, "de"));     // active language but not mapped
        Assert.Null(LanguageRoutes.FindFor(routes, "zz"));     // not a code at all
    }

    [Fact]
    public void FindFor_Null_Or_Empty_List_Returns_Null()
    {
        Assert.Null(LanguageRoutes.FindFor(null, "fa"));
        Assert.Null(LanguageRoutes.FindFor(new List<LanguageRoute>(), "fa"));
    }

    // ---- JSON round-trip (old settings.json migration) ----

    [Fact]
    public void RoutingSettings_Serialize_And_Deserialize_RoundTrip()
    {
        var settings = new AppSettings
        {
            LanguageRoutingEnabled = true,
            DetectLanguageAfterSpeech = false,
            LiveTranscription = false,
            LanguageDetectorModel = "base",
            ActiveLanguages = new List<string> { "fa", "de" },
            LanguageRoutes = new List<LanguageRoute>
            {
                new("fa", SpeechEngines.Whisper, "large-v3-turbo"),
                new("de", SpeechEngines.Vosk, "vosk-model-de"),
            },
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.True(restored!.LanguageRoutingEnabled);
        Assert.False(restored.DetectLanguageAfterSpeech);
        Assert.False(restored.LiveTranscription);
        Assert.Equal("base", restored.LanguageDetectorModel);
        Assert.Equal(new[] { "fa", "de" }, restored.ActiveLanguages);
        Assert.Equal(2, restored.LanguageRoutes.Count);
    }

    /// <summary>
    /// A settings.json from before this feature existed has none of the new fields. The initializers
    /// (and System.Text.Json honoring them) mean deserialization yields the documented defaults
    /// rather than nulls — so a routing-aware code path reading an old file never NREs.
    /// </summary>
    [Fact]
    public void RoutingSettings_MissingField_DeserializesToDefaults()
    {
        // A pre-routing settings.json.
        var json = "{\"Theme\":\"Dark\",\"SpeechEngine\":\"whisper\"}";

        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.False(restored!.LanguageRoutingEnabled);
        Assert.True(restored.DetectLanguageAfterSpeech);                // default: decide it at the end
        Assert.True(restored.LiveTranscription);                       // default true
        Assert.Equal("tiny", restored.LanguageDetectorModel);          // default tiny
        Assert.Equal(new[] { "fa", "en" }, restored.ActiveLanguages);  // default fa + en
        Assert.NotNull(restored.LanguageRoutes);
        Assert.Empty(restored.LanguageRoutes);
    }

    // ---- SettingsService.SanitizeLanguageRouting ----

    [Fact]
    public void Sanitize_Drops_Unknown_And_Duplicate_Codes()
    {
        var s = new AppSettings
        {
            ActiveLanguages = new List<string> { "FA", "fa", "zz", "en", "  de  " },
        };

        SettingsService.SanitizeLanguageRouting(s);

        // Uppercase/lowercase collapsed, unknown "zz" dropped, duplicates removed, order kept.
        Assert.Equal(new[] { "fa", "en", "de" }, s.ActiveLanguages);
    }

    [Fact]
    public void Sanitize_Drops_Routes_For_Inactive_Or_Empty_Models()
    {
        var s = new AppSettings
        {
            ActiveLanguages = new List<string> { "fa", "en" },
            LanguageRoutes = new List<LanguageRoute>
            {
                new("fa", SpeechEngines.Whisper, "large-v3-turbo"),  // keep: active + has model
                new("de", SpeechEngines.Whisper, "medium"),          // drop: de not active
                new("en", SpeechEngines.Vosk, ""),                   // drop: empty model
                new("en", SpeechEngines.Vosk, "vosk-model-en"),      // keep: first route for en
                new("en", SpeechEngines.Whisper, "base"),            // drop: duplicate en
            },
        };

        SettingsService.SanitizeLanguageRouting(s);

        Assert.Equal(2, s.LanguageRoutes.Count);
        Assert.Equal("fa", s.LanguageRoutes[0].Code);
        Assert.Equal("large-v3-turbo", s.LanguageRoutes[0].Model);
        Assert.Equal("en", s.LanguageRoutes[1].Code);
        Assert.Equal("vosk-model-en", s.LanguageRoutes[1].Model);
    }

    [Fact]
    public void Sanitize_Normalizes_Route_Engines()
    {
        var s = new AppSettings
        {
            ActiveLanguages = new List<string> { "fa" },
            LanguageRoutes = new List<LanguageRoute>
            {
                new("fa", "VOSK", "vosk-model-fa"),   // wrong case
                new("en", "bogus", "tiny"),           // unknown engine → whisper; but en not active → dropped
            },
        };

        SettingsService.SanitizeLanguageRouting(s);

        Assert.Single(s.LanguageRoutes);
        Assert.Equal(SpeechEngines.Vosk, s.LanguageRoutes[0].Engine);
    }

    /// <summary>
    /// An English-only (.en) model throws the moment it's asked to detect a language, so it can
    /// never be the detector. The sanitizer forces it back to the safe default.
    /// </summary>
    [Theory]
    [InlineData("tiny.en")]
    [InlineData("BASE.EN")]
    [InlineData("zz-unknown")]      // not in catalog
    [InlineData("")]                // missing
    [InlineData(null)]
    public void Sanitize_Rejects_Detector_That_Cannot_Detect(string? detector)
    {
        var s = new AppSettings { LanguageDetectorModel = detector! };

        SettingsService.SanitizeLanguageRouting(s);

        Assert.Equal("tiny", s.LanguageDetectorModel);
    }

    [Fact]
    public void Sanitize_Keeps_Valid_Multilingual_Detector()
    {
        var s = new AppSettings { LanguageDetectorModel = "base" };

        SettingsService.SanitizeLanguageRouting(s);

        Assert.Equal("base", s.LanguageDetectorModel);
    }

    [Fact]
    public void Sanitize_Null_Guards_Lists()
    {
        var s = new AppSettings { ActiveLanguages = null!, LanguageRoutes = null! };

        SettingsService.SanitizeLanguageRouting(s);

        Assert.NotNull(s.ActiveLanguages);
        Assert.NotNull(s.LanguageRoutes);
    }
}
