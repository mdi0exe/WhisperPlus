using System.Collections.Generic;
using System.Text.Json;
using WhisperPlus.Models;
using WhisperPlus.Services;
using Xunit;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Tests for the per-engine/model preset feature: the preset lookup helpers, the hotkey-action
/// key round-trip, and JSON persistence (including the null-on-missing-field migration handled
/// by <c>SettingsService</c>'s sanitizer).
/// </summary>
public class DictationPresetTests
{
    [Fact]
    public void FindFor_MatchesOnEngineAndModel()
    {
        var presets = new List<DictationPreset>
        {
            new("a", SpeechEngines.Whisper, "large-v3-turbo", "Ctrl+Shift+P"),
            new("b", SpeechEngines.Vosk, "vosk-model-small-en-us-0.15", "Ctrl+Shift+E"),
        };

        var match = DictationPresets.FindFor(presets, SpeechEngines.Vosk, "vosk-model-small-en-us-0.15");

        Assert.NotNull(match);
        Assert.Equal("b", match!.Id);
        Assert.Equal("Ctrl+Shift+E", match.Hotkey);
    }

    [Fact]
    public void FindFor_ReturnsNull_WhenNoMatch()
    {
        var presets = new List<DictationPreset>
        {
            new("a", SpeechEngines.Whisper, "tiny", "Ctrl+Shift+P"),
        };

        Assert.Null(DictationPresets.FindFor(presets, SpeechEngines.Whisper, "medium"));
    }

    [Fact]
    public void FindFor_NullList_ReturnsNull()
    {
        Assert.Null(DictationPresets.FindFor(null, SpeechEngines.Whisper, "tiny"));
    }

    [Fact]
    public void FindById_MatchesId()
    {
        var presets = new List<DictationPreset>
        {
            new("abc123", SpeechEngines.Whisper, "tiny", "Ctrl+Shift+P"),
        };

        Assert.NotNull(DictationPresets.FindById(presets, "abc123"));
        Assert.Null(DictationPresets.FindById(presets, "missing"));
    }

    [Theory]
    [InlineData("abc-123", "preset:abc-123", true)]
    [InlineData("x", "preset:x", true)]
    [InlineData("abc-123", "dictate", false)]      // a built-in action, not a preset
    [InlineData("", "preset:", false)]              // prefix with nothing after it
    public void PresetAction_RoundTrip(string id, string action, bool isPreset)
    {
        // PresetAction builds the key; TryGetPresetId must recover the id from it.
        var built = HotkeyActions.PresetAction(id);
        Assert.Equal("preset:" + id, built);

        var ok = HotkeyActions.TryGetPresetId(action, out var recovered);
        Assert.Equal(isPreset, ok);
        Assert.Equal(isPreset ? id : "", recovered);
    }

    [Fact]
    public void Presets_SerializeAndDeserialize_RoundTrip()
    {
        var settings = new AppSettings
        {
            ActiveWhisperModel = "large-v3-turbo",
            DictationPresets = new List<DictationPreset>
            {
                new("abc", SpeechEngines.Whisper, "large-v3-turbo", "Ctrl+Shift+P"),
                new("def", SpeechEngines.Vosk, "vosk-model-small-fa-0.42", "Ctrl+Shift+F"),
            },
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.DictationPresets.Count);
        Assert.Equal("Ctrl+Shift+P", restored.DictationPresets[0].Hotkey);
        Assert.Equal(SpeechEngines.Vosk, restored.DictationPresets[1].Engine);
    }

    /// <summary>
    /// A settings.json from before this feature existed has no DictationPresets field. Because
    /// the property is initialized to an empty list (and System.Text.Json honors that initializer),
    /// deserialization never yields null — consumers (startup registration, the UI) never see an
    /// NRE. The SettingsService sanitizer's `??= new()` remains as defense-in-depth.
    /// </summary>
    [Fact]
    public void Presets_MissingField_DeserializesToEmptyList()
    {
        // A pre-preset settings.json — note the absence of "DictationPresets".
        var json = "{\"Theme\":\"Dark\",\"AccentColor\":\"#6759FF\"}";

        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.DictationPresets);
        Assert.Empty(restored.DictationPresets);
    }
}
