using System;

namespace WhisperPlus.Models;

public static class PersianLayoutVariants
{
	public const string Auto = "auto";

	public const string Standard = "standard";

	public const string Legacy = "legacy";

	public static string Normalize(string? value)
	{
		string a = value?.Trim();
		if (string.Equals(a, "legacy", StringComparison.OrdinalIgnoreCase))
		{
			return "legacy";
		}
		if (string.Equals(a, "standard", StringComparison.OrdinalIgnoreCase))
		{
			return "standard";
		}
		return "auto";
	}
}
