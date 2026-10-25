using System;
using System.Collections.Generic;

namespace WhisperPlus.Models;

public static class LanguageIdModelCatalog
{
	public const string SileroLang95 = "lang_classifier_95";

	public const int SizeMb = 17;

	public const string Url = "https://huggingface.co/deepghs/silero-lang95-onnx/resolve/main/lang_classifier_95.onnx";

	public const string FileName = "lang_classifier_95.onnx";

	public const string InputName = "input";

	public const string OutputName = "output";

	public static readonly IReadOnlyList<string> ClassCodes = new string[95]
	{
		"fr", "zh", "ln", "fy-NL", "hi", "ru", "yo", "da", "it", "hr",
		"si", "as", "lo", "uk", "ko", "az", "gu", "nn", "zh-CN", "es",
		"lb", "ha", "id", "nl", "af", "pa-IN", "oc", "ps", "et", "fa",
		"en", "sw", "fi", "sq", "hy", "tt", "ta", "mn", "kk", "ht",
		"ja", "ur", "yi", "sv", "ca", "vi", "am", "sl", "th", "hu",
		"pl", "tg", "uz", "sn", "mk", "ar", "la", "be", "km", "mg",
		"ne", "te", "or", "pt", "tl", "sk", "ky", "my", "pa", "no",
		"ro", "el", "ml", "mt", "bo", "de", "tk", "sr", "bg", "cs",
		"tr", "so", "bn", "ms", "sv-SE", "zh-HK", "mr", "su", "rw", "ba",
		"bs", "sd", "gl", "kn", "zh-TW"
	};

	private static readonly Dictionary<string, int[]> CodeToIndices = BuildCodeToIndices();

	public static int ClassCount => ClassCodes.Count;

	private static Dictionary<string, int[]> BuildCodeToIndices()
	{
		Dictionary<string, List<int>> dictionary = new Dictionary<string, List<int>>(StringComparer.Ordinal);
		for (int i = 0; i < ClassCodes.Count; i++)
		{
			string text = BaseCode(ClassCodes[i]);
			if (text.Length == 2)
			{
				if (!dictionary.TryGetValue(text, out var value))
				{
					value = (dictionary[text] = new List<int>(1));
				}
				value.Add(i);
			}
		}
		Dictionary<string, int[]> dictionary2 = new Dictionary<string, int[]>(dictionary.Count, StringComparer.Ordinal);
		foreach (var (key, list3) in dictionary)
		{
			dictionary2[key] = list3.ToArray();
		}
		return dictionary2;
	}

	private static string BaseCode(string classCode)
	{
		int num = classCode.IndexOf('-');
		return ((num < 0) ? classCode : classCode.Substring(0, num)).ToLowerInvariant();
	}

	public static int[]? IndicesFor(string? code)
	{
		string text = LanguageCatalog.Normalize(code);
		if (text.Length != 2)
		{
			return null;
		}
		if (!CodeToIndices.TryGetValue(text, out int[] value))
		{
			return null;
		}
		return value;
	}

	public static bool Supports(string? code)
	{
		return IndicesFor(code) != null;
	}
}
