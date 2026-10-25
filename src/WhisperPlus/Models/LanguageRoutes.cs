using System;
using System.Collections.Generic;

namespace WhisperPlus.Models;

public static class LanguageRoutes
{
	public static LanguageRoute? FindFor(IEnumerable<LanguageRoute>? routes, string? code)
	{
		if (routes == null)
		{
			return null;
		}
		string text = LanguageCatalog.Normalize(code);
		if (text.Length == 0)
		{
			return null;
		}
		foreach (LanguageRoute route in routes)
		{
			if (string.Equals(route.Code, text, StringComparison.Ordinal))
			{
				return route;
			}
		}
		return null;
	}
}
