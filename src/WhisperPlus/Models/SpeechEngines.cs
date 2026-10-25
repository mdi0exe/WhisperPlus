using System;

namespace WhisperPlus.Models;

public static class SpeechEngines
{
	public const string Whisper = "whisper";

	public const string Vosk = "vosk";

	public const string Nemotron = "nemotron";

	/// <summary>
	/// The Bark engine: Parakeet-TDT transducers on sherpa-onnx's offline recognizer. Named after
	/// the Bark 3.0 family whose architecture it targets; see <see cref="BarkModelCatalog"/> for
	/// why the listed models are the ONNX exports of that architecture rather than Bark's own
	/// PyTorch weights.
	/// </summary>
	public const string Bark = "bark";

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
		if (string.Equals(a, "bark", StringComparison.OrdinalIgnoreCase))
		{
			return "bark";
		}
		return "whisper";
	}
}
