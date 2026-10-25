using System.Collections.Generic;

namespace WhisperPlus.Models;

public static class VoskModelCatalog
{
	public const string ModelsBase = "https://alphacephei.com/vosk/models";

	public static readonly IReadOnlyList<VoskModelInfo> Models = new VoskModelInfo[14]
	{
		new VoskModelInfo("vosk-model-small-fa-0.42", "Persian · Small (recommended)", 53, "fa"),
		new VoskModelInfo("vosk-model-small-fa-0.5", "Persian · Small (0.5)", 60, "fa"),
		new VoskModelInfo("vosk-model-fa-0.42", "Persian · Large vocabulary", 1600, "fa"),
		new VoskModelInfo("vosk-model-small-en-us-0.15", "English (US) · Small", 40, "en"),
		new VoskModelInfo("vosk-model-en-us-0.22-lgraph", "English (US) · Dynamic graph", 128, "en"),
		new VoskModelInfo("vosk-model-en-us-0.22", "English (US) · Accurate", 1800, "en"),
		new VoskModelInfo("vosk-model-small-de-0.15", "German · Small", 45, "de"),
		new VoskModelInfo("vosk-model-small-fr-0.22", "French · Small", 41, "fr"),
		new VoskModelInfo("vosk-model-small-es-0.42", "Spanish · Small", 39, "es"),
		new VoskModelInfo("vosk-model-small-it-0.22", "Italian · Small", 48, "it"),
		new VoskModelInfo("vosk-model-small-ru-0.22", "Russian · Small", 45, "ru"),
		new VoskModelInfo("vosk-model-small-tr-0.3", "Turkish · Small", 35, "tr"),
		new VoskModelInfo("vosk-model-small-hi-0.22", "Hindi · Small", 42, "hi"),
		new VoskModelInfo("vosk-model-small-cn-0.22", "Chinese · Small", 42, "zh")
	};

	public static string UrlFor(string id)
	{
		return "https://alphacephei.com/vosk/models/" + id + ".zip";
	}

	public static VoskModelInfo? Find(string? id)
	{
		if (string.IsNullOrEmpty(id))
		{
			return null;
		}
		foreach (VoskModelInfo model in Models)
		{
			if (model.Id == id)
			{
				return model;
			}
		}
		return null;
	}
}
