using System;
using System.Collections.Generic;

namespace WhisperPlus.Models;

public static class NemotronModelCatalog
{
	public const string HuggingFaceBase = "https://huggingface.co";

	public const string EncoderFile = "encoder.int8.onnx";

	public const string DecoderFile = "decoder.int8.onnx";

	public const string JoinerFile = "joiner.int8.onnx";

	public const string TokensFile = "tokens.txt";

	public static readonly IReadOnlyList<string> Files = new string[4] { "tokens.txt", "joiner.int8.onnx", "decoder.int8.onnx", "encoder.int8.onnx" };

	public static readonly IReadOnlyList<NemotronModelInfo> Models = new NemotronModelInfo[3]
	{
		new NemotronModelInfo("nemotron-3.5-streaming-0.6b-160ms", "Nemotron 3.5 · 160 ms chunks (lowest latency)", 651, "csukuangfj2/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-160ms-int8-2026-06-11", 160),
		new NemotronModelInfo("nemotron-3.5-streaming-0.6b-560ms", "Nemotron 3.5 · 560 ms chunks (recommended)", 651, "csukuangfj2/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11", 560),
		new NemotronModelInfo("nemotron-3.5-streaming-0.6b-1120ms", "Nemotron 3.5 · 1120 ms chunks (most accurate)", 651, "csukuangfj2/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-1120ms-int8-2026-06-11", 1120)
	};

	public const string DefaultModelId = "nemotron-3.5-streaming-0.6b-560ms";

	private static readonly HashSet<string> SupportedLanguages = new HashSet<string>(StringComparer.Ordinal)
	{
		"en", "es", "de", "fr", "it", "ar", "ja", "ko", "pt", "ru",
		"hi", "zh", "vi", "he", "nl", "cs", "da", "pl", "no", "sv",
		"th", "tr", "bg", "el", "et", "fi", "hr", "hu", "lt", "lv",
		"ro", "sk", "uk", "mt", "sl"
	};

	public static bool SupportsLanguage(string? code)
	{
		if (!string.IsNullOrEmpty(code))
		{
			return SupportedLanguages.Contains(code);
		}
		return false;
	}

	public static string UrlFor(string repository, string fileName)
	{
		return $"{"https://huggingface.co"}/{repository}/resolve/main/{fileName}";
	}

	public static NemotronModelInfo? Find(string? id)
	{
		if (string.IsNullOrEmpty(id))
		{
			return null;
		}
		foreach (NemotronModelInfo model in Models)
		{
			if (model.Id == id)
			{
				return model;
			}
		}
		return null;
	}
}
