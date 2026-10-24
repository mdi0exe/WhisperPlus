using System;
using System.Collections.Generic;
using WhisperPlus.Models;
using WhisperPlus.Services;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for the language-identification catalog and the window shaping both backends share.
///
/// <para>The class-index → language-code table is transcribed by hand from Silero's
/// <c>lang_dict_95.json</c>, and a single misplaced entry would silently route audio to the wrong
/// model — the failure would look like "detection is bad" rather than "the table is off by one".
/// These tests pin the indices that matter and the invariants that would catch a slip.</para>
/// </summary>
public class LanguageIdCatalogTests
{
    [Fact]
    public void Catalog_Has_Exactly_95_Classes()
    {
        Assert.Equal(95, LanguageIdModelCatalog.ClassCount);
        Assert.Equal(95, LanguageIdModelCatalog.ClassCodes.Count);
    }

    /// <summary>
    /// The two languages this app was built around. Their indices come straight from Silero's
    /// label map; if these are wrong, the headline feature is wrong.
    /// </summary>
    [Theory]
    [InlineData(29, "fa")]
    [InlineData(30, "en")]
    [InlineData(0, "fr")]
    [InlineData(75, "de")]
    [InlineData(55, "ar")]
    [InlineData(80, "tr")]
    [InlineData(5, "ru")]
    [InlineData(94, "zh-TW")]
    public void ClassCodes_Match_The_Silero_Label_Map(int index, string expected)
    {
        Assert.Equal(expected, LanguageIdModelCatalog.ClassCodes[index]);
    }

    [Fact]
    public void Persian_And_English_Map_To_Their_Own_Single_Class()
    {
        var fa = LanguageIdModelCatalog.IndicesFor("fa");
        var en = LanguageIdModelCatalog.IndicesFor("en");

        Assert.Equal(new[] { 29 }, fa);
        Assert.Equal(new[] { 30 }, en);
    }

    /// <summary>
    /// Region-tagged classes collapse onto their base code, so a vote for "zh-TW" counts as a vote
    /// for Chinese rather than being lost.
    /// </summary>
    [Fact]
    public void Region_Variants_Collapse_Onto_The_Base_Code()
    {
        var zh = LanguageIdModelCatalog.IndicesFor("zh");
        Assert.NotNull(zh);
        Assert.Equal(4, zh!.Length);                       // zh, zh-CN, zh-HK, zh-TW
        Assert.Contains(1, zh);
        Assert.Contains(18, zh);
        Assert.Contains(85, zh);
        Assert.Contains(94, zh);

        var sv = LanguageIdModelCatalog.IndicesFor("sv");
        Assert.NotNull(sv);
        Assert.Equal(2, sv!.Length);                       // sv, sv-SE

        var pa = LanguageIdModelCatalog.IndicesFor("pa");
        Assert.NotNull(pa);
        Assert.Equal(2, pa!.Length);                       // pa, pa-IN
    }

    [Fact]
    public void Lookup_Normalizes_Case_And_Whitespace()
    {
        Assert.Equal(new[] { 29 }, LanguageIdModelCatalog.IndicesFor("FA"));
        Assert.Equal(new[] { 29 }, LanguageIdModelCatalog.IndicesFor("  fa  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zz")]      // plausible code, no class in this model
    [InlineData("eng")]     // 3-letter
    public void Unrepresentable_Codes_Return_Null(string? code)
    {
        Assert.Null(LanguageIdModelCatalog.IndicesFor(code));
        Assert.False(LanguageIdModelCatalog.Supports(code));
    }

    /// <summary>
    /// Every index appears exactly once across all the code groups. A duplicated or skipped index
    /// in the hand-written table would double-count one language's probability and drop another's.
    /// </summary>
    [Fact]
    public void Every_Class_Index_Is_Claimed_Exactly_Once()
    {
        var seen = new HashSet<int>();
        foreach (var classCode in LanguageIdModelCatalog.ClassCodes)
        {
            var baseCode = classCode.Split('-')[0];
            var indices = LanguageIdModelCatalog.IndicesFor(baseCode);
            Assert.NotNull(indices);
            foreach (var index in indices!) seen.Add(index);
        }

        Assert.Equal(LanguageIdModelCatalog.ClassCount, seen.Count);
        for (var i = 0; i < LanguageIdModelCatalog.ClassCount; i++) Assert.Contains(i, seen);
    }

    /// <summary>
    /// Every language the app offers for routing should be identifiable, or the user could pick a
    /// language that detection can never return.
    /// </summary>
    [Fact]
    public void All_App_Languages_Are_Supported_By_The_Model()
    {
        foreach (var language in LanguageCatalog.Languages)
        {
            Assert.True(LanguageIdModelCatalog.Supports(language.Code),
                $"Silero has no class for '{language.Code}' ({language.EnglishName}), " +
                "which the app offers for routing.");
        }
    }

    [Fact]
    public void Model_Url_Points_At_The_Onnx_File()
    {
        Assert.EndsWith(".onnx", LanguageIdModelCatalog.Url, StringComparison.Ordinal);
        Assert.EndsWith(".onnx", LanguageIdModelCatalog.FileName, StringComparison.Ordinal);
        Assert.StartsWith("https://", LanguageIdModelCatalog.Url, StringComparison.Ordinal);
    }
}

/// <summary>Tests for the shared windowing helper both identifiers use.</summary>
public class LanguageIdWindowTests
{
    private const int SampleRate = 16000;

    [Fact]
    public void Pads_A_Short_Window_Up_To_The_Minimum()
    {
        var samples = new float[SampleRate / 4];      // 250 ms
        Array.Fill(samples, 0.5f);

        var window = LanguageIdWindow.Build(samples, SampleRate, minWindowMs: 1000, maxWindowMs: 4000);

        Assert.Equal(SampleRate, window.Length);      // padded to 1 s
        Assert.Equal(0.5f, window[0]);
        Assert.Equal(0f, window[^1]);                 // padding is silence, and it goes at the end
    }

    [Fact]
    public void Truncates_To_The_Maximum()
    {
        var samples = new float[SampleRate * 10];     // 10 s
        Array.Fill(samples, 0.5f);

        var window = LanguageIdWindow.Build(samples, SampleRate, minWindowMs: 1000, maxWindowMs: 4000);

        Assert.Equal(SampleRate * 4, window.Length);
    }

    [Fact]
    public void Keeps_A_Window_Already_In_Range()
    {
        var samples = new float[SampleRate * 2];
        Array.Fill(samples, 0.25f);

        var window = LanguageIdWindow.Build(samples, SampleRate, minWindowMs: 1000, maxWindowMs: 4000);

        Assert.Equal(SampleRate * 2, window.Length);
        Assert.All(window, value => Assert.Equal(0.25f, value));
    }

    /// <summary>
    /// The window is copied from the front of what it is handed. Callers pass audio already sliced
    /// to start at speech, so "the front" is the first speech — not the top of the capture.
    /// </summary>
    [Fact]
    public void Copies_From_The_Front_Of_The_Input()
    {
        var samples = new float[SampleRate * 6];
        for (var i = 0; i < samples.Length; i++) samples[i] = i;

        var window = LanguageIdWindow.Build(samples, SampleRate, minWindowMs: 1000, maxWindowMs: 2000);

        Assert.Equal(SampleRate * 2, window.Length);
        Assert.Equal(0f, window[0]);
        Assert.Equal(1f, window[1]);
        Assert.Equal(SampleRate * 2 - 1, window[^1]);
    }

    [Fact]
    public void Empty_Input_Yields_A_Silent_Minimum_Window()
    {
        var window = LanguageIdWindow.Build(Array.Empty<float>(), SampleRate, 1000, 4000);

        Assert.Equal(SampleRate, window.Length);
        Assert.All(window, value => Assert.Equal(0f, value));
    }
}

/// <summary>Tests for backend selection, which decides which model answers the question.</summary>
public class LanguageDetectorBackendTests
{
    [Theory]
    [InlineData("silero", LanguageDetectorBackends.Silero)]
    [InlineData("SILERO", LanguageDetectorBackends.Silero)]
    [InlineData("whisper", LanguageDetectorBackends.Whisper)]
    [InlineData("  Whisper ", LanguageDetectorBackends.Whisper)]
    public void Normalize_Accepts_Known_Backends(string input, string expected)
    {
        Assert.Equal(expected, LanguageDetectorBackends.Normalize(input));
    }

    /// <summary>
    /// An unknown value must fall back to the dedicated classifier rather than to Whisper's
    /// English-biased head — the whole point of the setting.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bogus")]
    public void Normalize_Defaults_To_Silero(string? input)
    {
        Assert.Equal(LanguageDetectorBackends.Silero, LanguageDetectorBackends.Normalize(input));
    }

    [Fact]
    public void Sanitizer_Repairs_An_Unknown_Backend()
    {
        var s = new AppSettings { LanguageDetectorBackend = "nonsense" };

        SettingsService.SanitizeLanguageRouting(s);

        Assert.Equal(LanguageDetectorBackends.Silero, s.LanguageDetectorBackend);
    }

    [Fact]
    public void Sanitizer_Keeps_An_Explicit_Whisper_Backend()
    {
        var s = new AppSettings { LanguageDetectorBackend = LanguageDetectorBackends.Whisper };

        SettingsService.SanitizeLanguageRouting(s);

        Assert.Equal(LanguageDetectorBackends.Whisper, s.LanguageDetectorBackend);
    }

    /// <summary>
    /// A settings.json written before the backend existed gets the new default, so an existing user
    /// is upgraded onto the better detector rather than left on the one that always said English.
    /// </summary>
    [Fact]
    public void Old_Settings_Default_To_Silero()
    {
        var json = "{\"Theme\":\"Dark\",\"LanguageRoutingEnabled\":true}";

        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(LanguageDetectorBackends.Silero, restored!.LanguageDetectorBackend);
    }

    /// <summary>
    /// The Whisper detector model is still sanitized when Silero is selected, because it remains
    /// the fallback if the Silero model has not been downloaded.
    /// </summary>
    [Fact]
    public void Whisper_Fallback_Model_Is_Sanitized_Even_Under_Silero()
    {
        var s = new AppSettings
        {
            LanguageDetectorBackend = LanguageDetectorBackends.Silero,
            LanguageDetectorModel = "tiny.en",   // cannot detect a language
        };

        SettingsService.SanitizeLanguageRouting(s);

        Assert.Equal("tiny", s.LanguageDetectorModel);
    }
}
