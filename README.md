# WhisperPlus

A production-ready Windows utility for **offline speech-to-text**, a **floating audio
recorder**, a **Persian ⇄ English keyboard-layout fixer**, and **screenshot annotation**.
Built with .NET 10 WPF and a Windows 11 Glass/Mica theme via
[WPF-UI](https://github.com/lepoco/wpf-ui).

```
┌──────────────────────────────────────────────────────────────┐
│  WhisperPlus                                                 │
│  ├─ 🎙  Offline dictation  (Whisper · Vosk · Nemotron)        │
│  ├─ 🔊  Floating recorder  (NAudio + live waveform)           │
│  ├─ ⌨  Layout fixer        (Persian ⇄ English converter)     │
│  └─ 📸  Screenshot         (region · annotate · Win+Shift+S)  │
│  + tray icon · global hotkeys · launch-on-startup · Mica UI  │
└──────────────────────────────────────────────────────────────┘
```

---

## Features

### 🎙 Feature 1 — Offline dictation
- **Three engines**, all fully offline, chosen in Settings → Voice models:

  | Engine | Library | Models | Character |
  |--------|---------|--------|-----------|
  | **Whisper** | [Whisper.net](https://github.com/sandbeach/whisper.net) + GGML | 77 MB – 3 GB | Most accurate, detects its own language, decodes the buffer in passes |
  | **Vosk** | Vosk/Kaldi | ~40–60 MB | Tiny and low-RAM, streams natively, one model per language |
  | **Nemotron** | [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) | ~650 MB | NVIDIA [Nemotron 3.5](https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b) — streams natively *and* transcribes near Whisper quality, multilingual with punctuation and capitalization |

- **Model manager (Settings → Voice models):** download any model from its catalog with a
  live progress bar, atomic `.part`-then-rename downloads, per-model disk size, delete, and
  "use as active" — plus an optional per-model global shortcut.
- **About Nemotron:** a cache-aware FastConformer-RNNT that neither Whisper.net nor Vosk can
  load, so it runs on sherpa-onnx. Three int8 exports are offered — 160 ms, 560 ms and 1120 ms
  chunks — which are the *same* model differing only in the latency/accuracy trade baked in at
  export time. It covers ~35 languages, and the routed language is passed to the model as a
  prompt (falling back to its own auto-detection when routing hasn't decided).
  **It does not support Persian** — keep Whisper for فارسی; the routing table will not offer
  Nemotron for a language it cannot transcribe.
- **Widget:** a small borderless, rounded pill docked just above the taskbar, with an
  animated audio waveform and Idle / Listening / Transcribing / Done states. A **close button**
  cancels the session outright — nothing is pasted, copied, or added to the history, even if the
  transcription was already running. Comes in two sizes (Settings → Appearance → *Dictation
  widget size*): the full pill, or a **compact** one at about a quarter of the area that shows a
  single line of text.
- **Flow:** global hotkey → capture 16 kHz mono audio → transcribe locally → paste the
  result into the focused field (clipboard preserved). The model unloads after each
  session to reclaim RAM. With language routing on (Feature 1a), capture feeds a
  `StreamingDictationSession` that detects the language, preloads the mapped model, and
  transcribes live; the final paste is near-instant because the text is already decoded.

### 🌐 Feature 1a — Real-time language routing & live transcription
- **Problem:** a bilingual speaker (Persian + English) had to pick the right model *before*
  speaking and wait for the full decode *after* stopping.
- **Language ID:** speech is run through a dedicated language classifier and the result shows as a
  **2-letter chip** on the widget (`fa`, `en`, …). *When* that happens is a setting — once you stop
  talking, from the whole recording (the default), or progressively from the first 1–2 s while you
  are still talking. Everything below applies to both; they differ only in what audio is available
  when the decision is taken.
  - **Speech-onset gated:** the window starts where you *start talking*, not where the mic
    opened. This matters more than it sounds — shown the silence between the hotkey and your
    first word, a classifier does not answer "don't know", it answers *English*. On a real
    Persian recording, classifying from the start of capture returns `en`; classifying from
    the onset returns `fa` at 99.7%.
  - **Two backends:** [Silero's 95-language classifier](https://huggingface.co/deepghs/silero-lang95-onnx)
    (ONNX, ~17 MB, MIT) by default — purpose-built for language ID, so it carries no English
    prior — with Whisper's own language head as the fallback when that model isn't downloaded.
  - **Candidate-restricted:** scoring is limited to your active languages, which turns a
    95-way problem into a 2-way one. The full softmax is summed per language and renormalized,
    and if your languages hold too little of the total mass the detector **declines** rather
    than inventing a winner — so silence and out-of-set speech leave routing on your default
    model instead of guessing.
  - **Evidence, not a single verdict:** no one window decides the session. Each look at the audio
    is pooled into a running score weighted by what it is actually worth — how long it was, how
    much of it was speech rather than pause, how much of the model's output landed on your
    languages at all, and how much of it had already been examined. The decision is made on the
    *margin* between the leading language and the runner-up, and committing to it requires more
    evidence than any single window can supply. A first answer is still routed to immediately, so
    the mapped model starts loading exactly as early as before; the search simply carries on
    behind it until the lead is wide enough to stop, and a second look can still overturn the
    first.
  - **A second look means somewhere new:** re-reading the opening second with a bit more on the
    end is not a second opinion, it is the same opinion — and counting it as independent is how a
    wrong first guess used to confirm itself. Detection tracks which speech it has examined and
    always reaches for a stretch it has not, skipping windows that turn out to be pauses. Audio
    that *is* re-read still counts, but only for the part that is new.
  - **The margin scales with the question:** a 3:1 lead among two languages and among eight are
    not the same claim, so the bar rises with the number of active languages instead of being one
    fixed probability for every case.
  - **Changing its mind costs something:** switching language mid-dictation means reloading a
    model and re-decoding every word so far, so a challenger has to beat the language in use by a
    wider margin — wider again each time, and only twice per session. No flickering between two
    models.
  - **Nothing is wasted at the end:** when you stop, whatever the live pass never reached is
    examined and pooled into the *same* score rather than judged by a separate contest, and the
    bar drops from "proven" to "worth acting on" — because the alternative is no longer more
    audio, it is transcribing with a model chosen by default.
- **Deciding when you stop, not while you talk** *(default — Settings → Languages → “Decide the
  language when I stop talking”)*: the shortcut's second press is what triggers identification, and it
  is answered against everything you said instead of the opening of it. The same schedule and the same
  accumulator do the work — it is the finishing pass with nothing before it, not a second algorithm —
  so every window it can afford is spent on the whole utterance. Nothing is ever routed to a language
  and then away from it again, which is what makes a model load twice and text get decoded twice.
  - **Why it is the default:** the opening of a sentence is the worst audio to judge, and it is the
    only audio the live pass has. On the Persian fixture the first four seconds are *declined*
    outright — the classifier finds no language it is confident about — and the answer (`fa`, 99.9%)
    comes from the audio after it. Deciding early means deciding on exactly the part that goes wrong.
  - **What it costs:** the mapped model cannot start loading until you stop, so a large one is waited
    on rather than being resident already, and with live transcription on there is nothing to show
    while you speak — which model would produce it is precisely what is not known yet. The widget says
    so (“Listening… · language when you stop”, then “Identifying language…”) rather than looking stuck.
  - **No pointless preload:** the default model is normally loaded the moment recording starts, on the
    theory that a wrong guess is free because you are still talking. Deferring removes that cover — the
    decision now lands when transcription has to start, and the engines load under a lock — so the
    preload is held back when *no* active language routes to that model, rather than queueing a model
    nobody needs in front of the one that is needed.
- **Background preloading:** the model mapped to the detected language starts loading the
  instant the language is known, so it is resident by the time you stop.
- **Live transcription:** text appears as you speak — Vosk and Nemotron stream natively (each
  finalizes an utterance when its endpointer fires); Whisper re-decodes only the uncommitted
  tail of the buffer on a rolling-window commit policy, so settled words are never re-decoded.
  - **Level-adaptive:** a quiet speaker used to get no answer at all. Every candidate window sat
    below the absolute speech threshold, so all of them were skipped as "pauses" and no window was
    ever classified. The pause yardstick is relative to how loud *you* are (kept above the measured
    room noise so real pauses are still skipped) and keeps adapting as you speak, and the window is
    lifted to a normal level before the model sees it — neither backend is level-invariant, so soft
    speech was a different input rather than merely a quieter one. Whether a window counts as speech
    is measured frame by frame, not as one average: four fifths silence with one loud syllable in it
    averages out the same as continuous speech, and a mean-level gate waved it straight through.
  - **A weak memory:** the languages you have actually been dictating in break ties that the audio
    leaves genuinely even, and Whisper's known English lean is corrected when it is the backend.
    Both are capped far below what one clean second of speech is worth, so they never outvote what
    you said — and so the history cannot quietly reinforce its own mistakes.
- **Manual override:** click the chip to switch languages on the fly (while recording or before
  the final paste); the target model is swapped and the buffer re-decoded with the correct model.
  "Automatic" is in that menu too, to undo a wrong pick.
- **Choosing the language yourself:** with routing **off** you can still say which language you
  speak — Settings → Languages → *Language I dictate in*, and the same chip on the widget.
  Your engine and model stay exactly as configured (switching models per language is what routing
  does, and you turned it off); the engine is simply told which language it is hearing instead of
  guessing per sentence, which is where short or quiet utterances go wrong.
- **Settings (Languages):** toggle routing, choose whether the language is decided as you speak or
  when you stop, pick the language to assume when routing is off, pick active languages, choose the
  detection backend (and download the Silero model), pick the Whisper fallback detector, and map each
  language → engine + model.
- **Clipboard-safe copy-last-transcript:** a hotkey (default `Ctrl + Alt + C`) puts the most
  recent transcript on the clipboard *without destroying what was there* — the prior contents
  are parked in an in-app ring. Both are reachable from the tray menu: **Recent Transcripts**
  (copy any of the last 25) and **Restore Clipboard** (put back any of the last 10 displaced
  snapshots).

### 🔊 Feature 2 — Floating pinned audio recorder
- **Widget:** compact, borderless, **Always-on-Top** (pinnable) window, draggable, with a
  live NAudio-driven level meter. Designed to stay visible while reading PDFs / browsing.
- **Controls:** on-screen buttons **and** global hotkeys for **Start**, **Pause/Resume**,
  **Stop & Save**. Pause mutes the writer rather than reopening the device (no latency). The
  **close button** is the opposite of Stop & Save: it cancels the recording and deletes the
  partial file, so closing the widget never leaves anything behind.
- **Settings (Audio):** microphone dropdown (NAudio `WaveIn` enumeration), a **Save recordings to
  disk** switch, output directory browser, and WAV/MP3 format. Capture is always PCM/WAV; an MP3
  request is transcoded on stop through Media Foundation (`MediaFoundationEncoder.EncodeToMp3`, no
  bundled encoder), and the intermediate WAV is removed. If the encoder is unavailable the WAV is
  kept and reported, so the result is always a file you can play.
- **Nothing on disk, if you prefer:** turn saving off and the recorder still runs — levels,
  waveform, hotkeys — but no file is ever created (not created-then-deleted), and the widget
  says so when you stop. Dictation never writes audio to disk either way; it is buffered in memory
  and discarded when the session ends.

### ⌨ Feature 3 — Persian ⇄ English layout fixer
- **Problem:** typing `kldnhkl` when you meant `نمیدانم` (wrong layout active).
- **Trigger:** configurable global hotkey (default **`Ctrl + Space`**).
- **Workflow:** detect the active selection (`Ctrl+C`); if none, fall back to selecting the last
  typed word (`Ctrl+Shift+Left`). Bidirectionally remap via the standard **ISIRI 2901** Persian
  keyboard layout, handling digits, Shift, and pass-through of unmapped symbols. Paste the
  corrected text back.
- **Your clipboard survives it.** The whole cycle borrows the clipboard twice — once to read the
  selection, once to paste the correction — so one **detached snapshot** is taken up front and put
  back at the end, on every path including the ones where nothing was converted. (Each step used to
  save and restore around itself, faithfully restoring what it found, which by the second step was
  the first step's scratch value — that is how the clipboard got destroyed. Holding the live
  `IDataObject` was the other half: it belongs to whichever app put it there and stops producing
  data once the clipboard is overwritten, so restoring it put back nothing.)
- **Two Persian layouts** (Settings → Shortcuts → *Persian layout*). Iran has two in circulation and
  they disagree on real letters, so the fixer has to invert the one you actually type with:

  | Key | Standard (ISIRI 9147) | Legacy (Arabic-derived) |
  |-----|----------------------|-------------------------|
  | `m` | پ | ئ |
  | `\` | pass-through | پ |
  | `'` | گ | گ |
  | digits | ۰–۹ | stay ASCII |
  | Shift+C / Shift+H | ژ / آ | ژ / آ |

  *Standard* matches Windows "Persian (Standard)" (KLID 00050429); *Legacy* matches the older
  "Persian (Farsi)" layout (KLID 00000429) — the legacy table was read straight from the installed
  Windows layout with `ToUnicodeEx`, not transcribed. The default is **Automatic**, which reads
  `HKCU\Keyboard Layout\Preload`: with only one Persian layout installed that is the answer, with
  both the standard one wins (a machine that types legacy can pin it in the same dropdown).
- **Shift is honoured, Caps Lock is not misread.** A capital inside otherwise-lowercase text is a
  real Shift press, so `C` → ژ and `H` → آ — and so is a *lone* capital (`C`, `H1`), because that is
  the only way to reach those letters. Caps Lock is only assumed from two or more capitals with no
  lowercase at all, so `SALAM` still becomes `سشمشپ`.
- **The tables are read from Windows, not from a chart.** Both layouts are dumped from the installed
  layouts with `ToUnicodeEx`, which is how the standard table's Shift layer is right: `K` → », `L` → «,
  `X` → ط, and `B` → ZWNJ (نیم‌فاصله).
- **The clipboard is left alone** (Settings → Shortcuts → *Don't use the clipboard*, on by default).
  The selection is read through UI Automation `TextPattern` and the correction is typed with
  `KEYEVENTF_UNICODE`, so nothing is copied and nothing is written: no entry in Windows clipboard
  history (Win+V), no clipboard-manager entry, and whatever the user had copied stays there. Apps
  that do not expose their text to accessibility — Chromium/Electron without accessibility enabled is
  the common case — fall back to the copy/paste cycle, clipboard restored afterwards.
- **Spacing survives.** The captured text is *not* trimmed: it is pasted back over the selection, so
  dropping a trailing space would glue the corrected word to the next one.
- **Optional language switch** (Settings → Shortcuts → *Switch input language after fixing*, on by
  default). After a successful fix the keyboard is switched to the language the text is now in —
  Persian after a fix to Persian, English after a fix to English — by posting
  `WM_INPUTLANGCHANGEREQUEST` to the foreground window, which is what Alt+Shift does.
- **The paste is given time to land.** `Ctrl+V` only asks the target app to paste, and browsers,
  Electron apps and Office read the clipboard a few frames later; restoring the user's clipboard
  immediately let them paste the *previous* contents. The restore now waits for the paste to settle,
  the capture polls the clipboard for up to 350 ms instead of sleeping a fixed 60 ms, a locked
  clipboard is retried once and reported instead of silently swallowed, and the "last word"
  fallback refuses anything containing whitespace (which is what a whole-line selection looks like).
- The conversion logic is isolated in `PersianEnglishLayoutConverter.cs` (pure, tested) and the
  key tables in `PersianKeyboardLayouts.cs`.

### 📸 Feature 4 — Screenshot & annotation
A Lightshot-style capture layer, ported from [screen_forge](https://github.com/ruwiss/screen_forge)
and folded into the app instead of shipped beside it: same process, same tray icon, same settings
file, and **no extra memory at idle**.

- **Trigger:** `Win + Shift + S` (rebindable in Settings → Shortcuts). By default the keystroke is
  *consumed*, so the Windows Snipping Tool does not open as well — WhisperPlus becomes the handler
  for that shortcut. Turn *Replace Windows Snipping Tool* off to let both react.
- **Why a keyboard hook rather than a registered hotkey:** `Win+Shift+S` belongs to the shell, and
  `RegisterHotKey` cannot take it. A `WH_KEYBOARD_LL` hook sees the key first, swallows it, and
  starts the capture — the same mechanism AutoHotkey uses for this combination.
- **Start menu stays closed:** swallowing the trigger leaves the shell with Win down and up and
  nothing in between, which is a bare Windows-key tap. A mask keypress is injected in between (the
  `{vkE8}` trick), and no modifier is ever swallowed, so the keyboard state stays truthful.
- **Capture:** one full-screen overlay over the frozen desktop. Drag a region or switch to
  *Full screen*; the overlay spans the whole virtual desktop and the chrome follows the monitor
  the cursor is on.
- **Annotate:** select/move, rectangle, ellipse, arrow, line, pen, highlighter, text, numbered
  steps and blur/pixelate — with colour, stroke width, fill, font size/weight and step shape.
  Undo/redo, delete, arrow-key nudging, `Ctrl+C` copy, `Ctrl+S` save (PNG/JPEG/WebP), `Esc` cancel.
  Every style change from the option bar is a history entry of its own, and a slider drag collapses
  into one — so `Ctrl+Z` undoes the colour you just picked, not the shape you drew before it.
  A drawn item is *not* auto-selected: a drawing tool cannot act on the selection box, so it only
  appears for the Select tool (`V`), where the handles actually move and resize.
- **Looks like the rest of the app:** the chrome uses the same floating-widget tokens as the
  dictation and recorder pills (`Wp.Widget.*`) plus the user's own accent (`Wp.Accent`), so the
  tool rail, export bar and option bar follow the chosen theme and accent colour live.
- **Clipboard:** the result is published as `CF_DIB`, `CF_DIBV5` (with alpha), `PNG` and
  `image/png`, so Paint, Office, browsers and screenshot viewers all accept the paste.
- **OCR (offline):** the export bar's *OCR* button reads the text out of the selection with the OCR
  engine that ships with Windows (`Windows.Media.Ocr`) — nothing is downloaded and nothing leaves the
  machine. The recognised lines are copied to the clipboard; when no OCR language pack is installed
  the button says so instead of failing silently.
- **Colour picker:** the *Colour* button arms an eyedropper; the next click reads the pixel from the
  untouched capture, copies its hex value to the clipboard, and adopts it as the annotation colour.
- **Pin to screen:** the *Pin* button keeps the snip — annotations included — as a floating,
  always-on-top window. Drag anywhere to move it, scroll to zoom (20 %–400 %), right-click for
  Copy/Actual size/Close, `Esc` closes. The overlay itself closes once the pin is up, and the pin
  holds only a frozen image, so nothing of the capture is retained.
- **Memory:** nothing of the drawing stack loads until the first capture, and the overlay releases
  its screen-sized buffers on close (`CaptureOverlayWindow.Dispose`), including every annotation's
  Skia caches and the snapshots on the undo stack (`Scene.ReleaseResources`). Measured on a 1920×1080
  desktop: ~50 MB while the overlay is open, back to the pre-capture private working set
  (~47 MB) once it closes. The idle footprint is unchanged because the SkiaSharp assemblies and
  the overlay's bitmaps are only touched on demand.
- **Command line:** `WhisperPlus.exe --capture` opens a capture immediately and then stays in the
  tray — handy for binding a screenshot key in another launcher.
- **Limitation:** the hook, like every non-elevated tool, cannot intercept input while an elevated
  window has focus.

---

## Architecture

```
src/WhisperPlus/
├── App.xaml(.cs)                Generic Host + Serilog + DI wiring + hotkey/tray bootstrap
├── Models/                      AppSettings, Whisper/Vosk/Nemotron catalogs, LanguageRouting
├── Interop/                     Win32 (RegisterHotKey/WM_HOTKEY), DwmWindow (Mica/rounded),
│                                VirtualKeyboard (SendInput chords + modifier clearing)
├── Services/
│   ├── SettingsService.cs       JSON settings (atomic write) at %APPDATA%\WhisperPlus
│   ├── HotkeyService.cs         Win32 hotkeys on a message-only window
│   ├── TrayService.cs           H.NotifyIcon tray + context menu + clipboard history
│   ├── StartupService.cs        HKCU Run-key toggle for launch-on-startup
│   ├── ThemeService.cs          WPF-UI dark/light/system + accent
│   ├── DiPageProvider.cs        DI-aware page provider for NavigationView
│   ├── AudioDeviceService.cs    enumerate WaveIn devices
│   ├── AudioRecorder.cs         NAudio Start/Pause/Resume/Stop & save
│   ├── WaveformAggregator.cs    RMS level sampling → bindable meter
│   ├── WhisperModelManager.cs   GGML download w/ progress + file management
│   ├── WhisperService.cs        Whisper.net factory + ProcessAsync (lazy load/unload, segment API)
│   ├── VoskService.cs           Vosk recognizer + live streaming VoskStream
│   ├── NemotronModelManager.cs  four-file sherpa-onnx bundle download + file management
│   ├── NemotronService.cs       sherpa-onnx OnlineRecognizer + live streaming NemotronStream
│   ├── LanguageDetector.cs      backend selection (Silero ⇄ Whisper) + per-session fallback
│   ├── ILanguageIdentifier.cs   contract shared by both language-ID backends
│   ├── SileroLanguageIdentifier.cs  ONNX 95-language classifier, candidate-mass gated
│   ├── WhisperLanguageIdentifier.cs Whisper language head (fallback backend)
│   ├── LanguageIdModelManager.cs    downloads/locates the Silero ONNX file
│   ├── SpeechOnsetDetector.cs   energy VAD: finds where speech starts, so LID sees speech
│   ├── LanguageRouter.cs        detected code → mapped engine/model + background preload
│   ├── RollingCommitPolicy.cs   pure split of a rolling Whisper decode into committed/preview
│   ├── StreamingDictationSession.cs  one-session pipeline: detect→route→preload→live decode
│   ├── TranscriptHistory.cs     ring of recent transcripts + displaced-clipboard ring
│   ├── ClipboardAccess.cs       IClipboardAccess + WPF STA-thread implementation
│   ├── LanguageIdWindow.cs      bounds + level-normalizes the window handed to a detector
│   ├── TextInjector.cs          Ctrl+V paste, with optional clipboard save/restore
│   ├── TextSelectionService.cs  capture selection / last word (caller owns the clipboard)
│   ├── PersianEnglishLayoutConverter.cs   ISIRI 2901 bidirectional map
│   └── LayoutFixService.cs      orchestrate capture → convert → inject
├── ScreenCapture/               Feature 4 — capture + annotation (ported from screen_forge)
│   ├── ScreenshotService.cs     hotkey → hide own widgets → grab → show overlay → release
│   ├── ScreenshotHotkeyHook.cs  WH_KEYBOARD_LL shortcut that can take over Win+Shift+S
│   ├── CaptureOverlayWindow.xaml(.cs)  selection + tool rail + options + export, one window
│   ├── ScreenCaptureCore.cs     BitBlt of the virtual desktop, crop, BitmapSource conversion
│   ├── ToolStyleMemory.cs       last-used colour/width/font/step/blur, kept between captures
│   ├── ChromeScale.cs           per-monitor chrome scaling for the overlay
│   ├── Icons.xaml, OverlayTheme.xaml   self-contained dark chrome (not the app's Aurora theme)
│   └── Editor/                  Scene (items + undo), SceneRenderer, InteractiveCanvas,
│                                ShapeItems, TextItem, StepBlurImageItems, ImageExporter,
│                                SceneClipboard, CanvasViewTransform
├── ViewModels/                  ViewModelBase
├── Views/
│   ├── MainWindow.xaml(.cs)     NavigationView shell (General/Hotkeys/Whisper/Audio/Languages/About)
│   ├── Pages/                   GeneralPage, HotkeysPage, WhisperPage, AudioPage, LanguagePage, AboutPage
│   ├── Widgets/                 RecorderWidget, DictationWidget (borderless, topmost, rounded)
│   └── Controls/                WaveformControl (animated bars)
└── Helpers/                     HotkeyGesture (parse/format gesture strings)

tests/WhisperPlus.Tests/         xUnit — converter, hotkey gestures, presets, language routing,
                               rolling-commit policy, language router, transcript history,
                               speech onset + language-ID window level
```

### Idle efficiency
- Widgets are **lazy-created** (no cost until first hotkey/tray trigger) and reused.
- Capture devices are disposed on stop; the Whisper model is unloaded after each
  dictation session; the waveform `DispatcherTimer` stops when its widget hides.
- Hotkeys use a native **message-only window** (no visual tree) and `MOD_NOREPEAT`.
- The screenshot stack is **loaded on demand**: the overlay, the SkiaSharp assemblies and the
  screen-sized bitmaps exist only between the shortcut and the copy/save, and are disposed when
  the overlay closes. The keyboard hook that owns the shortcut is a single callback with no
  timer and no polling.

### Data locations
| Path | Contents |
|------|----------|
| `%APPDATA%\WhisperPlus\settings.json` | All persisted settings |
| `%APPDATA%\WhisperPlus\logs\` | Rolling daily Serilog logs (7-day retention) |
| `%APPDATA%\WhisperPlus\models\` | Downloaded GGML model files (Vosk under `vosk\`, Nemotron under `nemotron\`) |
| `Documents\WhisperPlus Recordings\` | Recordings (configurable) |

---

## Build & run

Requires the **.NET 10 SDK** (the solution is pinned via `global.json`). Windows only.

```bash
# from the repo root
dotnet restore WhisperPlus.sln
dotnet build   WhisperPlus.sln -c Debug

# run (WPF needs an interactive desktop session)
dotnet run --project src/WhisperPlus/WhisperPlus.csproj
```

### Run the tests
```bash
dotnet test WhisperPlus.sln
```
The suite covers the layout converter (`kldnhkl ↔ نمیدانم`, full ISIRI 2901 rows,
digits, shifted-symbol pass-through, case collapse, round-trip), the hotkey-gesture
parser, dictation presets, language catalog/route helpers and the settings sanitizer,
the rolling-commit windowing policy, the language router's resolve/fallback logic,
and the transcript history's clipboard-preservation guarantee.

### Publish a self-contained build
```bash
dotnet publish src/WhisperPlus/WhisperPlus.csproj -c Release -r win-x64 \
    -p:Platform=x64 --self-contained false -o publish
```
(Use `--self-contained true` to bundle the .NET runtime for a no-installer portable build.)

---

## Default hotkeys (all rebindable in Settings → Hotkeys)

| Action | Default |
|--------|---------|
| Toggle dictation | `Ctrl + Shift + D` |
| Fix keyboard layout | `Ctrl + Space` |
| Copy last transcript | `Ctrl + Alt + C` |
| Recorder — Start | `Ctrl + Alt + R` |
| Recorder — Pause/Resume | `Ctrl + Alt + P` |
| Recorder — Stop & Save | `Ctrl + Alt + S` |
| Capture region (screenshot) | `Win + Shift + S` |

---

## Key dependencies
- [WPF-UI](https://www.nuget.org/packages/WPF-UI) `4.3.0` — Fluent + Mica/Acrylic
- [Whisper.net](https://www.nuget.org/packages/Whisper.net) `1.9.1` + `Whisper.net.Runtime` — offline STT
- [Vosk](https://www.nuget.org/packages/Vosk) `0.3.38` — lightweight streaming STT
- [org.k2fsa.sherpa.onnx](https://www.nuget.org/packages/org.k2fsa.sherpa.onnx) `1.13.4` — runs the
  Nemotron streaming transducer. Note it ships its own `onnxruntime.dll`; the build keeps the newer
  one from `Microsoft.ML.OnnxRuntime` (see the comment in `WhisperPlus.csproj`)
- [NAudio](https://www.nuget.org/packages/NAudio) `2.3.0` — audio capture/playback
- [H.NotifyIcon.Wpf](https://www.nuget.org/packages/H.NotifyIcon.Wpf) `2.4.1` — system tray
- [CommunityToolkit.Mvvm](https://www.nuget.org/packages/CommunityToolkit.Mvvm) `8.4.2` — MVVM source generators
- [SkiaSharp](https://www.nuget.org/packages/SkiaSharp) `4.148.0` + `SkiaSharp.Views.WPF` — renders
  the screenshot scene and the annotations. Loaded on the first capture only.
- [Serilog](https://www.nuget.org/packages/Serilog.Extensions.Hosting) — structured logging

---

## Notes
- **First run** of dictation requires downloading a model in Settings → Voice models.
  The Whisper Tiny model (~77 MB) is a good starting point; Nemotron 560 ms is the one to pick
  if you want live, punctuated transcription and can spare ~650 MB.
- Mica/Acrylic effects require **Windows 11**; on older builds the windows fall back to
  normal chrome (the DWM attribute calls no-op gracefully).
- Live GUI behaviour (tray icon, hotkey firing, mic capture, Whisper inference) must be
  verified in an interactive desktop session; the unit tests cover the pure logic.
