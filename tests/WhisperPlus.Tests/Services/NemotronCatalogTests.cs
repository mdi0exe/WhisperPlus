using System;
using System.Linq;
using WhisperPlus.Models;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for <see cref="NemotronModelCatalog"/> and the engine-id normalization that had to grow
/// a third value for it. These are pure lookups — nothing here loads sherpa-onnx or touches disk.
/// </summary>
public class NemotronCatalogTests
{
    [Fact]
    public void Catalog_Ids_Are_Unique_And_Findable()
    {
        var ids = NemotronModelCatalog.Models.Select(m => m.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var id in ids) Assert.NotNull(NemotronModelCatalog.Find(id));
    }

    [Fact]
    public void Find_Unknown_Or_Empty_Id_Returns_Null()
    {
        Assert.Null(NemotronModelCatalog.Find("not-a-model"));
        Assert.Null(NemotronModelCatalog.Find(""));
        Assert.Null(NemotronModelCatalog.Find(null));
    }

    /// <summary>The default has to be a real catalog entry, or first-run dictation targets nothing.</summary>
    [Fact]
    public void Default_Model_Is_In_The_Catalog()
    {
        Assert.NotNull(NemotronModelCatalog.Find(NemotronModelCatalog.DefaultModelId));
    }

    /// <summary>
    /// Every model must name a distinct repository, and every file the manager downloads has to be
    /// one the URL builder can address — a typo here is a 404 half a gigabyte into a download.
    /// </summary>
    [Fact]
    public void Every_Model_Has_A_Distinct_Repository_And_Four_Files()
    {
        var repos = NemotronModelCatalog.Models.Select(m => m.Repository).ToList();
        Assert.Equal(repos.Count, repos.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(4, NemotronModelCatalog.Files.Count);
        Assert.Contains(NemotronModelCatalog.EncoderFile, NemotronModelCatalog.Files);
        Assert.Contains(NemotronModelCatalog.DecoderFile, NemotronModelCatalog.Files);
        Assert.Contains(NemotronModelCatalog.JoinerFile, NemotronModelCatalog.Files);
        Assert.Contains(NemotronModelCatalog.TokensFile, NemotronModelCatalog.Files);
    }

    /// <summary>
    /// The encoder is ~650 MB and the token list ~128 KB, so the manager fetches smallest-first to
    /// surface a broken URL in seconds rather than after the bulk of the transfer.
    /// </summary>
    [Fact]
    public void Files_Are_Ordered_Smallest_First_So_Failures_Surface_Early()
    {
        Assert.Equal(NemotronModelCatalog.TokensFile, NemotronModelCatalog.Files[0]);
        Assert.Equal(NemotronModelCatalog.EncoderFile, NemotronModelCatalog.Files[^1]);
    }

    [Fact]
    public void UrlFor_Builds_A_HuggingFace_Resolve_Url()
    {
        var model = NemotronModelCatalog.Find(NemotronModelCatalog.DefaultModelId)!;

        var url = NemotronModelCatalog.UrlFor(model.Repository, NemotronModelCatalog.TokensFile);

        Assert.Equal($"https://huggingface.co/{model.Repository}/resolve/main/tokens.txt", url);
    }

    // ---- Language support ----

    /// <summary>
    /// Persian is this app's primary language and Nemotron cannot transcribe it, so the routing
    /// table must never offer it there. This is the guard that keeps that true.
    /// </summary>
    [Fact]
    public void Persian_Is_Not_Supported()
    {
        Assert.False(NemotronModelCatalog.SupportsLanguage("fa"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("ar")]
    [InlineData("uk")]
    [InlineData("zh")]
    public void Documented_Languages_Are_Supported(string code)
    {
        Assert.True(NemotronModelCatalog.SupportsLanguage(code));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("zz")]
    public void Unknown_Or_Missing_Codes_Are_Not_Supported(string? code)
    {
        Assert.False(NemotronModelCatalog.SupportsLanguage(code));
    }

    // ---- Engine id ----

    [Theory]
    [InlineData("nemotron")]
    [InlineData("Nemotron")]
    [InlineData("  NEMOTRON  ")]
    public void Normalize_Recognizes_The_Nemotron_Engine(string stored)
    {
        Assert.Equal(SpeechEngines.Nemotron, SpeechEngines.Normalize(stored));
    }

    /// <summary>Adding a third engine must not have disturbed the existing two, or every
    /// settings.json in the wild would silently re-route.</summary>
    [Theory]
    [InlineData("vosk", SpeechEngines.Vosk)]
    [InlineData("whisper", SpeechEngines.Whisper)]
    [InlineData("something-else", SpeechEngines.Whisper)]
    [InlineData("", SpeechEngines.Whisper)]
    [InlineData(null, SpeechEngines.Whisper)]
    public void Normalize_Still_Handles_The_Other_Engines(string? stored, string expected)
    {
        Assert.Equal(expected, SpeechEngines.Normalize(stored));
    }
}
