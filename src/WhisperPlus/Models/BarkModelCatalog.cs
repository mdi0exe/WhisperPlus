using System;
using System.Collections.Generic;

namespace WhisperPlus.Models;

/// <summary>
/// The models the Bark engine can run, and the language facts that decide routing.
///
/// <para>Bark 3.0 Pro — <c>ConwayResearch/Underdog-Bark-3.0-Pro</c> — is the model this engine was
/// added for, but it cannot be listed here directly: Underdog publishes PyTorch safetensors,
/// Core ML packages and MLX weights, and no ONNX. The app runs sherpa-onnx, which needs ONNX, so
/// the only way to run Bark's weights today would be to export them first. What is listed instead
/// is the ONNX export of the model Bark 3.0 Pro is built on — NVIDIA's Parakeet-TDT 0.6B v3, in
/// the same four-file layout — which is the architecture this engine drives.</para>
///
/// <para>That difference matters for expectations: Bark 3.0 Pro is English-only, while the v3
/// export underneath it is multilingual. Both are European-language models — neither speaks
/// Persian, so this engine is an English/European option alongside Whisper, not a replacement for
/// it in the Persian ⇄ English workflow.</para>
/// </summary>
public static class BarkModelCatalog
{
    public const string HuggingFaceBase = "https://huggingface.co";

    public const string EncoderFile = "encoder.int8.onnx";

    public const string DecoderFile = "decoder.int8.onnx";

    public const string JoinerFile = "joiner.int8.onnx";

    public const string TokensFile = "tokens.txt";

    /// <summary>
    /// The bundle, smallest file first so a failed download leaves the least to redo. All four
    /// must be present before the folder counts as installed.
    /// </summary>
    public static readonly IReadOnlyList<string> Files = new[] { TokensFile, JoinerFile, DecoderFile, EncoderFile };

    public static readonly IReadOnlyList<BarkModelInfo> Models = new[]
    {
        new BarkModelInfo(
            "parakeet-tdt-0.6b-v3",
            "Bark 3.0 Pro base · Parakeet TDT 0.6B v3 (25 languages, recommended)",
            670,
            "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
            EnglishOnly: false),

        new BarkModelInfo(
            "parakeet-tdt-0.6b-v2",
            "Parakeet TDT 0.6B v2 · English only",
            670,
            "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
            EnglishOnly: true),
    };

    public const string DefaultModelId = "parakeet-tdt-0.6b-v3";

    /// <summary>
    /// The 25 European languages the v3 export recognizes. Taken from NVIDIA's model card; the
    /// model detects the language itself, so this set is only used to keep routing from mapping a
    /// language it would silently mistranscribe.
    /// </summary>
    private static readonly HashSet<string> MultilingualLanguages = new(StringComparer.Ordinal)
    {
        "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it",
        "lv", "lt", "mt", "pl", "pt", "ro", "sk", "sl", "es", "sv", "ru", "uk",
    };

    /// <summary>
    /// Whether <paramref name="modelId"/> can transcribe <paramref name="code"/>. The English-only
    /// export accepts nothing but <c>en</c>; the multilingual one accepts its 25 languages.
    /// </summary>
    public static bool SupportsLanguage(string? modelId, string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;

        var info = Find(modelId);
        if (info is null) return false;

        return info.EnglishOnly
            ? string.Equals(code, "en", StringComparison.Ordinal)
            : MultilingualLanguages.Contains(code);
    }

    /// <summary>True when any listed model can transcribe <paramref name="code"/>.</summary>
    public static bool EngineSupportsLanguage(string? code)
    {
        foreach (var model in Models)
        {
            if (SupportsLanguage(model.Id, code)) return true;
        }
        return false;
    }

    public static string UrlFor(string repository, string fileName)
    {
        return $"{HuggingFaceBase}/{repository}/resolve/main/{fileName}";
    }

    public static BarkModelInfo? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        foreach (var model in Models)
        {
            if (model.Id == id) return model;
        }
        return null;
    }
}
