using System;
using System.Collections.Generic;

namespace WhisperPlus.Models;

/// <summary>
/// The models the Bark engine can run, and the language facts that decide routing.
///
/// <para>These are Conway Research's Underdog Bark 3.0 family — the same weights Underdog ships —
/// in the community sherpa-onnx conversion by <c>velddev</c>. That conversion is what makes them
/// runnable here at all: the originals are published as a packed <c>model.bark</c> blob plus
/// PyTorch / Core ML / MLX runtimes, none of which sherpa-onnx can open, while the conversion
/// reproduces them as the four-file NeMo transducer bundle this engine loads.</para>
///
/// <para><b>Sizes are not the sizes on the model cards.</b> Bark 3.0 is advertised as a 163 MB
/// download because <c>model.bark</c> stores its weights compressed. Unpacked into int8 ONNX
/// graphs the same weights occupy ~685 MB, and that is what actually lands on disk — quoting 163 MB
/// in the UI would promise a download five times smaller than the real one.</para>
///
/// <para><b>Bark 3.0 Pro (1.25 GB) is absent, and cannot be added here.</b> It is the most accurate
/// member of the family, but no ONNX conversion of it exists — only the packed blob, GGUF and MLX
/// builds — so there is nothing this engine could load. Bark 3.0 sits within a few tenths of a WER
/// point of it at a fifth of the size.</para>
///
/// <para>Every Bark 3.0 model is <b>English only</b>; the family is not multilingual, unlike the
/// Parakeet model it derives from. Persian is not supported by any of them.</para>
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
            "bark-3.0",
            "Bark 3.0 · best size to accuracy, keeps disfluencies (~685 MB)",
            685,
            "velddev/Underdog-Bark-3.0-sherpa-onnx",
            EnglishOnly: true),

        new BarkModelInfo(
            "bark-3.0-plus",
            "Bark 3.0 Plus · larger sibling, slightly more accurate (~685 MB)",
            685,
            "velddev/Underdog-Bark-3.0-Plus-sherpa-onnx",
            EnglishOnly: true),

        new BarkModelInfo(
            "bark-3.0-clean",
            "Bark 3.0 Clean · same model, leaves out “um” and “uh” (~685 MB)",
            685,
            "velddev/Underdog-Bark-3.0-Clean-sherpa-onnx",
            EnglishOnly: true),
    };

    public const string DefaultModelId = "bark-3.0";

    /// <summary>
    /// Whether <paramref name="modelId"/> can transcribe <paramref name="code"/>. Every Bark 3.0
    /// export is English-only, so this stays narrow on purpose rather than carrying a language
    /// table: claiming more would route German or Persian at fluent nonsense.
    /// </summary>
    public static bool SupportsLanguage(string? modelId, string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;

        var info = Find(modelId);
        if (info is null) return false;

        return !info.EnglishOnly || string.Equals(code, "en", StringComparison.Ordinal);
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
