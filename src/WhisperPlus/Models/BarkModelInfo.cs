namespace WhisperPlus.Models;

/// <summary>
/// One downloadable Bark engine model: a NeMo Parakeet-TDT bundle exported to sherpa-onnx's
/// four-file ONNX layout (encoder / decoder / joiner / tokens).
/// </summary>
/// <param name="Id">Stable id used in settings, folder names and preset lookups.</param>
/// <param name="DisplayName">Text shown in Settings → Voice models.</param>
/// <param name="SizeMb">Approximate download size, for the UI before anything is installed.</param>
/// <param name="Repository">HuggingFace repository holding the four ONNX files.</param>
/// <param name="EnglishOnly">
/// True for the English-only v2 export. It decides whether a language code can be routed to this
/// model, and whether the UI warns that the model ignores the detected language.
/// </param>
public sealed record BarkModelInfo(
    string Id,
    string DisplayName,
    int SizeMb,
    string Repository,
    bool EnglishOnly);
