using System;

namespace WhisperPlus.Models;

public static class SpeechEngines
{
	public const string Whisper = "whisper";

	public const string Vosk = "vosk";

	public const string Nemotron = "nemotron";

	public static string Normalize(string? value)
	{
		string a = value?.Trim();
		if (string.Equals(a, "vosk", StringComparison.OrdinalIgnoreCase))
		{
			return "vosk";
		}
		if (string.Equals(a, "nemotron", StringComparison.OrdinalIgnoreCase))
		{
			return "nemotron";
		}
		return "whisper";
	}
}
