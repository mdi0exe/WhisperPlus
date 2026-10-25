using System.Collections.Generic;

namespace WhisperPlus.Models;

public static class WhisperModelCatalog
{
	public const string HuggingFaceBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

	public static readonly IReadOnlyList<WhisperModelInfo> Models = new WhisperModelInfo[10]
	{
		new WhisperModelInfo("tiny", "Tiny (fastest, ~1 GB RAM)", 77),
		new WhisperModelInfo("tiny.en", "Tiny · English-only", 77),
		new WhisperModelInfo("base", "Base (balanced, ~1.5 GB RAM)", 147),
		new WhisperModelInfo("base.en", "Base · English-only", 147),
		new WhisperModelInfo("small", "Small (~2.5 GB RAM)", 488),
		new WhisperModelInfo("small.en", "Small · English-only", 488),
		new WhisperModelInfo("medium", "Medium (~5 GB RAM)", 1531),
		new WhisperModelInfo("medium.en", "Medium · English-only", 1531),
		new WhisperModelInfo("large-v3-turbo", "Large v3 Turbo (Persian recommended, ~1.6 GB)", 1625),
		new WhisperModelInfo("large-v3", "Large v3 (~10 GB RAM)", 3100)
	};

	public static string FileNameFor(string id)
	{
		return "ggml-" + id + ".bin";
	}

	public static string UrlFor(string id)
	{
		return "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileNameFor(id);
	}

	public static WhisperModelInfo? Find(string? id)
	{
		if (string.IsNullOrEmpty(id))
		{
			return null;
		}
		foreach (WhisperModelInfo model in Models)
		{
			if (model.Id == id)
			{
				return model;
			}
		}
		return null;
	}
}
