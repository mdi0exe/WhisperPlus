using System.Collections.Generic;

namespace WhisperPlus.Models;

public static class LanguageCatalog
{
	public static readonly IReadOnlyList<LanguageOption> Languages = new LanguageOption[15]
	{
		new LanguageOption("fa", "Persian", "فارسی"),
		new LanguageOption("en", "English", "English"),
		new LanguageOption("de", "German", "Deutsch"),
		new LanguageOption("fr", "French", "Français"),
		new LanguageOption("es", "Spanish", "Español"),
		new LanguageOption("it", "Italian", "Italiano"),
		new LanguageOption("ru", "Russian", "Русский"),
		new LanguageOption("tr", "Turkish", "Türkçe"),
		new LanguageOption("ar", "Arabic", "العربية"),
		new LanguageOption("hi", "Hindi", "ह\u093fन\u094dद\u0940"),
		new LanguageOption("zh", "Chinese", "中文"),
		new LanguageOption("nl", "Dutch", "Nederlands"),
		new LanguageOption("pt", "Portuguese", "Português"),
		new LanguageOption("ja", "Japanese", "日本語"),
		new LanguageOption("ko", "Korean", "한국어")
	};

	public static string Normalize(string? code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return "";
		}
		string text = code.Trim().ToLowerInvariant();
		if (text.Length != 2)
		{
			return "";
		}
		return text;
	}

	public static LanguageOption? Find(string? code)
	{
		string text = Normalize(code);
		if (text.Length == 0)
		{
			return null;
		}
		foreach (LanguageOption language in Languages)
		{
			if (language.Code == text)
			{
				return language;
			}
		}
		return null;
	}

	public static bool IsKnown(string? code)
	{
		return (object)Find(code) != null;
	}

	public static string DisplayName(string? code)
	{
		LanguageOption languageOption = Find(code);
		if ((object)languageOption == null)
		{
			string text = Normalize(code);
			if (text == null || text.Length != 2)
			{
				return "—";
			}
			return text;
		}
		if (!(languageOption.NativeName == languageOption.EnglishName))
		{
			return languageOption.NativeName + " · " + languageOption.EnglishName;
		}
		return languageOption.EnglishName;
	}
}
