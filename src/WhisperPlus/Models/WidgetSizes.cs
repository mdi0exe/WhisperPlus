using System;

namespace WhisperPlus.Models;

public static class WidgetSizes
{
	public const string Large = "Large";

	public const string Small = "Small";

	public static string Normalize(string? value)
	{
		if (!string.Equals(value?.Trim(), "Small", StringComparison.OrdinalIgnoreCase))
		{
			return "Large";
		}
		return "Small";
	}
}
