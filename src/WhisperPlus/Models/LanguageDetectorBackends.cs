using System;

namespace WhisperPlus.Models;

public static class LanguageDetectorBackends
{
	public const string Silero = "silero";

	public const string Whisper = "whisper";

	public static string Normalize(string? value)
	{
		if (!string.Equals(value?.Trim(), "whisper", StringComparison.OrdinalIgnoreCase))
		{
			return "silero";
		}
		return "whisper";
	}
}
