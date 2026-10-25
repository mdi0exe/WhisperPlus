using System.Collections.Generic;

namespace WhisperPlus.Models;

public sealed class AppSettings
{
	public string Theme { get; set; } = "Dark";

	public string AccentColor { get; set; } = "#6759FF";

	public bool EnableBackdrop { get; set; } = true;

	public bool LaunchOnStartup { get; set; }

	public bool MinimizeToTray { get; set; } = true;

	public string SpeechEngine { get; set; } = "whisper";

	public string ActiveWhisperModel { get; set; } = "tiny";

	public string ActiveVoskModel { get; set; } = "vosk-model-small-en-us-0.15";

	public string ActiveNemotronModel { get; set; } = "nemotron-3.5-streaming-0.6b-560ms";

	public string ActiveBarkModel { get; set; } = "bark-3.0";

	public string WhisperModelsDirectory { get; set; } = "";

	public bool DictationHoldToTalk { get; set; }

	public string DictationWidgetSize { get; set; } = "Large";

	public List<DictationPreset> DictationPresets { get; set; } = new List<DictationPreset>();

	public bool LanguageRoutingEnabled { get; set; }

	public bool DetectLanguageAfterSpeech { get; set; } = true;

	public List<string> ActiveLanguages { get; set; } = new List<string> { "fa", "en" };

	public List<LanguageRoute> LanguageRoutes { get; set; } = new List<LanguageRoute>();

	public string DictationLanguage { get; set; } = "";

	public string LanguageDetectorBackend { get; set; } = "silero";

	public string LanguageDetectorModel { get; set; } = "tiny";

	public bool LiveTranscription { get; set; } = true;

	public string RecordingOutputDirectory { get; set; } = "";

	public string RecordingFormat { get; set; } = "wav";

	public int RecordingDeviceIndex { get; set; } = -1;

	public bool SaveRecordings { get; set; } = true;

	public string HotkeyDictate { get; set; } = "Ctrl+Shift+D";

	public string HotkeyFixLayout { get; set; } = "Ctrl+Space";

	public string HotkeyRecorderStart { get; set; } = "Ctrl+Alt+R";

	public string HotkeyRecorderPause { get; set; } = "Ctrl+Alt+P";

	public string HotkeyRecorderStop { get; set; } = "Ctrl+Alt+S";

	public string HotkeyCopyLastTranscript { get; set; } = "Ctrl+Alt+C";

	public string PersianLayout { get; set; } = "auto";

	public bool LayoutFixClipboardFree { get; set; } = true;

	public bool LayoutFixSwitchLanguage { get; set; } = true;

	public bool ScreenshotEnabled { get; set; } = true;

	public string HotkeyScreenshot { get; set; } = "Win+Shift+S";

	public bool ScreenshotReplaceWindowsSnipping { get; set; } = true;

	public string ScreenshotSaveDirectory { get; set; } = "";

	public string ScreenshotFormat { get; set; } = "png";

	public int ScreenshotQuality { get; set; } = 92;

	public static AppSettings CreateDefault()
	{
		return new AppSettings();
	}
}
