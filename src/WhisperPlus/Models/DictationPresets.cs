using System.Collections.Generic;

namespace WhisperPlus.Models;

public static class DictationPresets
{
	public static DictationPreset? FindFor(IEnumerable<DictationPreset>? presets, string engine, string model)
	{
		if (presets == null)
		{
			return null;
		}
		foreach (DictationPreset preset in presets)
		{
			if (preset.Engine == engine && preset.Model == model)
			{
				return preset;
			}
		}
		return null;
	}

	public static DictationPreset? FindById(IEnumerable<DictationPreset>? presets, string id)
	{
		if (presets == null)
		{
			return null;
		}
		foreach (DictationPreset preset in presets)
		{
			if (preset.Id == id)
			{
				return preset;
			}
		}
		return null;
	}
}
