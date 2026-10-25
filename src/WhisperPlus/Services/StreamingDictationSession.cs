using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>Everything one dictation session needs to know up front.</summary>
/// <param name="InitialTarget">Engine + model to start with, before any language is known.</param>
/// <param name="RoutingEnabled">Whether to identify the language and re-route to its model.</param>
/// <param name="Candidates">The languages detection may choose between.</param>
/// <param name="LiveTranscription">Whether to transcribe continuously while the user speaks.</param>
/// <param name="SampleRate">Capture rate; 16 kHz for every engine.</param>
/// <param name="RecentLanguages">
/// Languages of this run's recent dictations, most recent first, or null for none. Used only as a
/// tie-breaking prior — see <see cref="LanguagePrior"/>, which caps how much it can ever matter.
/// </param>
/// <param name="DeferDetectionUntilStop">
/// Whether to leave the language undecided until the user stops, and then decide it once against the
/// whole recording instead of progressively while they are still speaking.
/// </param>
public sealed record SessionOptions(
    RouteTarget InitialTarget,
    bool RoutingEnabled,
    IReadOnlyList<string> Candidates,
    bool LiveTranscription,
    int SampleRate = 16000,
    IReadOnlyList<string?>? RecentLanguages = null,
    bool DeferDetectionUntilStop = false);

/// <summary>
/// Drives one dictation session: audio arrives from the widget's capture callback, and this class
/// identifies the language, re-routes to the model mapped to it, preloads that model while the
/// user is still talking, and transcribes continuously so the text is essentially ready the
/// moment they stop.
///
/// <para>All model work happens on a single background pump task, which keeps the engines — each
/// of which is stateful and none of which is thread-safe — free of races, and keeps the UI
/// thread free of multi-second decodes. Events are raised from that pump thread, so subscribers
/// must marshal to the UI themselves.</para>
///
/// <para>When the language changes (a re-detection or the user correcting the widget's guess),
/// the model changes with it and everything decoded so far is thrown away and re-decoded, because
/// text produced by the wrong-language model is simply wrong. Switches only happen in the first
/// couple of seconds or on a deliberate click, so the wasted work is bounded.</para>
///
/// <para><b>How the language is decided.</b> Not by whichever window first crossed a probability
/// threshold — that is one model call deciding a session, and it is what made detection unreliable.
/// <see cref="LanguageProbeScheduler"/> picks stretches of the utterance that have not been examined
/// yet, <see cref="LanguageEvidenceAccumulator"/> pools what each one says weighted by how much it is
/// worth, and the route changes only when the leader's margin over the runner-up justifies it. An
/// early leader is routed to <em>tentatively</em> — the model starts loading immediately, so the
/// latency is what it always was — while the search carries on until the margin is wide enough to
/// commit, or the audio runs out.</para>
///
/// <para><b>Deciding it only at the end instead.</b> With
/// <see cref="SessionOptions.DeferDetectionUntilStop"/> the search does not run while the user speaks:
/// the language is decided once, when they press the shortcut again, against the whole utterance. The
/// same scheduler and the same accumulator do it — this is the finishing pass with nothing before it,
/// not a second algorithm — so it sees every word rather than the opening of the sentence, and it can
/// never route to a language and away from it again, which is what makes a model get loaded twice and
/// text get decoded twice. What it costs is the head start: the mapped model cannot begin loading
/// until the recording ends, so a large one is waited on. Live decoding is held back too while it is
/// pending, since text from a model that is about to be replaced would only be thrown away.</para>
/// </summary>
public sealed class StreamingDictationSession : IDisposable
{
    /// <summary>Longest the pump sleeps before re-checking, even with no new audio.</summary>
    private const int PollMs = 120;

    /// <summary>
    /// Cap on language inferences per pump tick. A window that answers nothing moves straight to the
    /// next one instead of waiting for the next tick — when the audio is already buffered there is
    /// nothing to wait for — but the pump also drives live decoding, and the Whisper backend's windows
    /// are encoder passes, so a run of declines must not stall it.
    /// </summary>
    private const int MaxProbesPerTick = 2;

    /// <summary>
    /// Length of the first language window. Short on purpose: nothing can be routed until it answers,
    /// so this sets how soon the mapped model starts loading.
    /// </summary>
    private const int FirstProbeMs = 1200;

    /// <summary>
    /// Ceiling on language windows per session. Generous, because a window that finds nothing is
    /// cheap and the alternative is giving up on a long utterance in a hard room, but bounded so
    /// identification cannot eat a whole recording.
    /// </summary>
    private const int MaxProbeWindows = 10;

    /// <summary>
    /// How much of a window must be speech before it is worth an inference. Below this the classifier
    /// is being shown mostly silence, which is not a hard question it answers badly but an easy one it
    /// answers confidently and wrongly.
    /// </summary>
    private const double MinVoicedRatio = 0.35;

    /// <summary>
    /// How long to wait for a speech onset before giving up and detecting on the loudest audio seen
    /// so far. Without this a session that never crosses the speech threshold — a very quiet
    /// speaker, a distant mic — would never route at all, which is worse than a best guess.
    /// </summary>
    private const int OnsetTimeoutMs = 2500;

    /// <summary>Minimum gap between rolling Whisper decodes.</summary>
    private const int DecodeIntervalMs = 1200;

    /// <summary>Whisper needs a reasonable window; below this the output is mostly noise.</summary>
    private const int MinLiveTailMs = 1000;

    /// <summary>New audio required before spending another decode on the same tail.</summary>
    private const int MinNewAudioMs = 600;

    private readonly WhisperService _whisper;
    private readonly VoskService _vosk;
    private readonly NemotronService _nemotron;
    private readonly BarkService _bark;
    private readonly LanguageDetector _detector;
    private readonly LanguageRouter _router;
    private readonly SessionOptions _options;

    /// <summary>
    /// Pools every look at the audio into one decision, instead of letting whichever window happened
    /// to clear a probability threshold first decide the session on its own.
    /// </summary>
    private LanguageEvidenceAccumulator? _evidence;

    /// <summary>Chooses which stretch of the utterance to examine next.</summary>
    private LanguageProbeScheduler? _probes;

    /// <summary>How many times the route has already changed language; feeds the switching penalty.</summary>
    private int _languageSwitches;

    /// <summary>Whether the language currently applied was a committed decision or a tentative one.</summary>
    private bool _languageCommitted;

    private readonly object _bufferGate = new();
    private readonly List<float> _buffer = new();

    /// <summary>Index in <see cref="_buffer"/> where the not-yet-committed window starts.</summary>
    private int _windowStart;

    /// <summary>Index in <see cref="_buffer"/> up to which audio has been fed to Vosk.</summary>
    private int _voskFed;

    /// <summary>Index in <see cref="_buffer"/> up to which audio has been fed to Nemotron.</summary>
    private int _nemotronFed;

    /// <summary>
    /// Locates the first sample of actual speech, so language identification is shown speech rather
    /// than the room tone at the top of the capture.
    /// </summary>
    private readonly SpeechOnsetDetector _onset;

    /// <summary>Index in <see cref="_buffer"/> up to which audio has been fed to <see cref="_onset"/>.</summary>
    private int _onsetFed;

    /// <summary>
    /// The sample every language window is measured from, fixed once resolved. See
    /// <see cref="ResolveDetectionStart"/> for why it must not move.
    /// </summary>
    private int? _detectionOrigin;

    private readonly SemaphoreSlim _signal = new(0);
    private int _signalled;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _decodeCts;
    private Task? _pump;

    private RouteTarget _target;
    private Task<bool>? _load;
    private Task<bool>? _detectorLoad;
    private VoskStream? _voskStream;
    private NemotronStream? _nemotronStream;

    private string _committed = "";
    private string _preview = "";

    /// <summary>
    /// Bumped whenever decoded state is thrown away, so a decode that was already in flight can
    /// tell its result belongs to a superseded model and drop it.
    /// </summary>
    private int _generation;

    /// <summary>Guards against telling the user the language was unclear more than once.</summary>
    private bool _unclearReported;

    /// <summary>Language windows skipped without an inference because they were mostly pause.</summary>
    private int _windowsSilent;

    /// <summary>Language windows that cost an inference and came back with nothing usable.</summary>
    private int _windowsDeclined;

    /// <summary>Language windows that produced an observation, whatever it was worth.</summary>
    private int _windowsAnswered;

    /// <summary>True once the language is decided and detection should stop spending cycles.</summary>
    private bool _languageSettled;

    private bool _manualOverride;
    private string? _pendingOverride;

    /// <summary>Logged once, so a session that fell back to the loudest window says so exactly once.</summary>
    private bool _onsetFallbackLogged;

    private string? _pinnedLanguage;
    private bool _hasPinned;

    private long _lastDecodeTick;
    private int _lastDecodedTail;
    private volatile bool _finishRequested;
    private bool _disposed;

    public StreamingDictationSession(
        WhisperService whisper,
        VoskService vosk,
        NemotronService nemotron,
        BarkService bark,
        LanguageDetector detector,
        LanguageRouter router,
        SessionOptions options)
    {
        _whisper = whisper;
        _vosk = vosk;
        _nemotron = nemotron;
        _bark = bark;
        _detector = detector;
        _router = router;
        _options = options;
        _target = options.InitialTarget;
        _onset = new SpeechOnsetDetector(options.SampleRate);
    }

    /// <summary>Raised when the routed engine/model or the detected language changes.</summary>
    public event Action<RouteTarget>? TargetChanged;

    /// <summary>Raised with the full live text (committed + unsettled tail) as it changes.</summary>
    public event Action<string>? PreviewChanged;

    /// <summary>Raised with short human-readable progress notes for the widget's status line.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>The engine + model this session is currently transcribing with.</summary>
    public RouteTarget Target => _target;

    /// <summary>The detected (or manually chosen) 2-letter language code, if any.</summary>
    public string? DetectedLanguage => _target.LanguageCode;

    /// <summary>True once the user has corrected the language by hand.</summary>
    public bool IsManualOverride => _manualOverride;

    /// <summary>True when the routed model could not be loaded, so no text could be produced.</summary>
    public bool ModelLoadFailed { get; private set; }

    /// <summary>True while the routed model is still being loaded.</summary>
    public bool IsModelLoading => _load is { IsCompleted: false };

    /// <summary>Starts the background pump. Call once, right after capture begins.</summary>
    public void Start()
    {
        if (_pump is not null) return;
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    /// <summary>
    /// Adds newly captured samples. Called from the audio callback thread, so it does no more
    /// than copy them in and poke the pump.
    /// </summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;
        lock (_bufferGate)
        {
            foreach (var sample in samples) _buffer.Add(sample);
        }
        Signal();
    }

    /// <summary>
    /// Switches to a language the user picked by hand, overriding (and disabling) automatic
    /// detection for the rest of the session. Safe to call from the UI thread; the switch itself
    /// happens on the pump, and any decode already running is abandoned so it takes effect now.
    /// </summary>
    public void OverrideLanguage(string languageCode)
    {
        var code = LanguageCatalog.Normalize(languageCode);
        if (code.Length != 2) return;
        QueueOverride(code);
    }

    /// <summary>
    /// Hands the language back to the engine: no pinned language, so Whisper decides per decode.
    /// The counterpart to <see cref="OverrideLanguage"/> for a user who picked one by mistake, and
    /// the only way back to automatic when routing is off and there is no detection to resume.
    /// </summary>
    public void OverrideToAutomatic() => QueueOverride(AutoOverride);

    /// <summary>Sentinel for "no language", which a null pending slot cannot express.</summary>
    private const string AutoOverride = "auto";

    private void QueueOverride(string code)
    {
        _pendingOverride = code;
        try { _decodeCts?.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        Signal();
    }

    /// <summary>
    /// Stops the session, finishes decoding whatever is left, and returns the final transcript.
    /// Because the committed part was decoded while the user was speaking, this normally only has
    /// the short unsettled tail left to do.
    /// </summary>
    public async Task<string> FinishAsync()
    {
        _finishRequested = true;
        Signal();

        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* cancelled: return whatever settled */ }
        }

        return _committed.Trim();
    }

    /// <summary>
    /// Completes when the decode loop has returned. Everything the session borrows from the engines
    /// (models, streams) is idle from that moment on, which is what makes it safe to unload them.
    /// </summary>
    public Task WhenPumpStoppedAsync() => _pump ?? Task.CompletedTask;

    /// <summary>Abandons the session without finishing the transcription.</summary>
    public void Cancel()
    {
        _finishRequested = true;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { /* already gone */ }
        Signal();
    }

    // ---- Pump ----

    private async Task PumpAsync(CancellationToken ct)
    {
        // Start both loads immediately: the transcription model the session will need, and (only
        // if routing can actually change the answer) the detector that picks it.
        if (ShouldPreloadInitialTarget()) _load = _router.PreloadAsync(_target, ct);
        if (DetectionPossible) _detectorLoad = _detector.EnsureReadyAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested && !_finishRequested)
            {
                await WaitForWorkAsync(ct).ConfigureAwait(false);

                ApplyPendingOverride();

                // Kept running even when the decision is deferred: this is what measures how loud the
                // speaker actually is, and the end-of-recording pass judges which windows are speech
                // against that. Feeding it only later would judge the whole utterance by its opening.
                AdvanceOnset();

                if (!_options.DeferDetectionUntilStop) TryDetectLanguage();

                if (_options.LiveTranscription && !AwaitingDeferredLanguage)
                {
                    // A live decode is best-effort: a hiccup here must not lose the session, since
                    // the final pass re-decodes whatever is still uncommitted anyway.
                    try { await DecodeAsync(final: false, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    catch (Exception ex) { Log.Warning(ex, "Live decode failed; continuing."); }
                }
            }

            ct.ThrowIfCancellationRequested();

            // Final pass: settle the language against the whole recording if the live probes never
            // managed to, wait for the model (it may still be loading on a very short utterance),
            // then decode everything that has not been committed yet.
            ApplyPendingOverride();
            await WaitForDetectorAsync(ct).ConfigureAwait(false);
            SettleLanguage();
            if (!await EnsureModelReadyAsync(ct).ConfigureAwait(false))
            {
                ModelLoadFailed = true;
                return;
            }
            await DecodeAsync(final: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _voskStream?.Dispose();
            _voskStream = null;
            _nemotronStream?.Dispose();
            _nemotronStream = null;
        }
    }

    /// <summary>
    /// Whether the default model is worth loading before anything is known about the language.
    ///
    /// <para>Normally yes, unconditionally: the load overlaps with the user speaking, so a wrong guess
    /// costs nothing they wait for. Deferring detection removes that cover. The decision now lands at
    /// the same moment the transcription has to start, and the engines load under a lock — so a model
    /// that no language could have routed to would not merely be wasted, it would sit in front of the
    /// one that <em>is</em> needed and be waited on first. When at least one candidate language routes to
    /// this model the bet is still worth making; when none do, it is a guaranteed loss.</para>
    /// </summary>
    private bool ShouldPreloadInitialTarget()
    {
        if (!AwaitingDeferredLanguage) return true;

        foreach (var code in _options.Candidates)
        {
            if (_router.Resolve(code).SameModelAs(_target)) return true;
        }

        Log.Debug("Holding the {Model} preload: no active language routes to it and the language is " +
                  "decided when recording stops.", _target.Model);
        return false;
    }

    /// <summary>Sleeps until there is new audio, a pending request, or the poll interval elapses.</summary>
    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(PollMs, ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _signalled, 0);
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref _signalled, 1) == 0)
        {
            try { _signal.Release(); }
            catch (ObjectDisposedException) { /* session already disposed */ }
        }
    }

    // ---- Language identification ----

    /// <summary>
    /// Detection only earns its keep when routing is on and there is more than one language it
    /// could pick — with a single active language the answer is already known.
    /// </summary>
    private bool DetectionPossible => _options.RoutingEnabled && _options.Candidates.Count > 1;

    /// <summary>
    /// True while the route is deliberately unknown: detection was deferred to the end of the
    /// recording, and until it runs there is no way to tell which model will be transcribing. Decoding
    /// through it would produce text with whichever model happened to be the default and then throw all
    /// of it away the moment the language turns out to be a different one — CPU spent to display words
    /// that are about to be retracted. A hand-picked language ends the wait immediately, because then
    /// the model is known.
    /// </summary>
    private bool AwaitingDeferredLanguage =>
        _options.DeferDetectionUntilStop && DetectionPossible && !_languageSettled && !_manualOverride;

    /// <summary>
    /// Waits out the detector's load before the final pass, but only when that pass is the session's
    /// only chance to identify anything. Detection during speech can afford to skip a tick and look
    /// again — there is always more audio coming — whereas a deferred decision that gave up because the
    /// model was still opening would mean never detecting at all, which is exactly what the user asked
    /// for and did not get.
    /// </summary>
    private async Task WaitForDetectorAsync(CancellationToken ct)
    {
        if (!_options.DeferDetectionUntilStop || !DetectionPossible) return;
        if (_manualOverride || _detectorLoad is null) return;

        try
        {
            if (!await _detectorLoad.ConfigureAwait(false))
            {
                StatusChanged?.Invoke($"Language detector ({_detector.DetectorModelId}) unavailable");
                Log.Warning("No language detector available; transcribing with the default model.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* nothing to settle */ }
        catch (Exception ex)
        {
            Log.Warning(ex, "Language detector failed to load; transcribing with the default model.");
        }
    }

    /// <summary>
    /// Feeds newly captured audio to the onset detector. Runs on the pump rather than in
    /// <see cref="Append"/> so all analysis stays on one thread and the audio callback keeps doing
    /// nothing but copying.
    ///
    /// <para>Feeding continues past the onset, unlike before. The detector's notion of how loud this
    /// speaker is — which is what <see cref="SpeechOnsetDetector.PauseThreshold"/> scales to, and
    /// therefore what decides which windows count as speech — is only as good as the audio it has
    /// heard. Stopping at the onset froze that estimate at the first syllable, so a speaker who warmed
    /// up after their first word was judged against their quietest moment for the rest of the session.
    /// The cost is one RMS pass per 20 ms frame.</para>
    /// </summary>
    private void AdvanceOnset()
    {
        if (!DetectionPossible) return;

        int from, to;
        lock (_bufferGate)
        {
            from = _onsetFed;
            to = _buffer.Count;
            if (to <= from) return;
            _onsetFed = to;
        }

        _onset.Feed(Snapshot(from, to - from));
    }

    /// <summary>
    /// Where the detection window should start, or null while it is still worth waiting for the user
    /// to begin speaking. After <see cref="OnsetTimeoutMs"/> the loudest audio so far is used
    /// instead, since a best guess beats never routing at all.
    ///
    /// <para>Latched the first time it resolves. Every offset the scheduler and the accumulator record
    /// is measured from this point, so it has to mean the same thing for the whole session — and the
    /// loudest-frame fallback does not stand still: it moves to wherever the peak currently is, which
    /// would silently re-base the coverage map each time a louder syllable arrived, and windows already
    /// examined would appear to sit somewhere they never did.</para>
    /// </summary>
    private long? ResolveDetectionStart()
    {
        if (_detectionOrigin is { } latched) return latched;

        long resolved;
        if (_onset.OnsetSample is { } onset)
        {
            resolved = onset;
        }
        else
        {
            if (_onset.ElapsedMs < OnsetTimeoutMs) return null;

            if (!_onsetFallbackLogged)
            {
                _onsetFallbackLogged = true;
                Log.Debug("No speech onset within {Ms} ms; detecting from the loudest audio so far.", OnsetTimeoutMs);
            }
            resolved = _onset.BestGuessOnsetSample;
        }

        _detectionOrigin = (int)resolved;
        return resolved;
    }

    /// <summary>
    /// Builds the accumulator and the schedule, once the detector has resolved which backend is
    /// answering. That resolution matters to both: the window sizes come from the backend's own
    /// limits, and the prior corrects the bias of that particular model.
    /// </summary>
    private void EnsureDetectionState()
    {
        if (_evidence is not null) return;

        var backend = _detector.ActiveBackend;
        var prior = LanguagePrior.Build(_options.Candidates, backend, _options.RecentLanguages);

        _evidence = new LanguageEvidenceAccumulator(
            _options.Candidates,
            prior,
            new LanguageEvidenceOptions { ReferenceLengthMs = _detector.MaxWindowMs });

        // The opening window is short only because something is waiting on it — the model preload, which
        // cannot start until a language is named. A deferred decision has nothing waiting on it: the
        // recording is already over, so the first window may as well be as long as the backend can use,
        // which is more evidence per inference and fewer inferences to reach the margin.
        _probes = new LanguageProbeScheduler(
            firstWindowMs: _options.DeferDetectionUntilStop ? _detector.MaxWindowMs : FirstProbeMs,
            windowMs: _detector.MaxWindowMs,
            minWindowMs: _detector.MinWindowMs,
            maxWindows: MaxProbeWindows);

        Log.Debug("Language identification: {Backend}, {Count} candidates, commit margin {Margin:0.00} nats, " +
                  "prior [{Prior}].",
            backend, _evidence.Candidates.Count, _evidence.CommitMarginNats, prior);
    }

    private void TryDetectLanguage()
    {
        if (!DetectionPossible || _manualOverride || _languageSettled) return;

        // If no detector model is installed (or the load failed), stop trying and say so once.
        if (_detectorLoad is { IsCompleted: true } && !IsCompletedTrue(_detectorLoad))
        {
            _languageSettled = true;
            StatusChanged?.Invoke($"Language detector ({_detector.DetectorModelId}) unavailable");
            return;
        }
        if (!_detector.IsReady) return; // still loading; try again on the next tick

        var start = ResolveDetectionStart();
        if (start is null) return;      // nothing worth classifying yet

        EnsureDetectionState();

        // Overlapping windows are refused while the user is still talking: waiting a moment for speech
        // nobody has looked at yet is strictly better than paying to re-read what has been.
        ExamineWindows((int)start.Value, allowOverlap: false, budget: MaxProbesPerTick);

        // Out of windows with nothing to show for them. Say so now rather than leaving the widget's
        // chip blank until the user stops talking.
        if (_probes!.IsExhausted && _target.LanguageCode is null)
        {
            _languageSettled = true;
            ReportLanguageUnclear();
        }
    }

    /// <summary>
    /// Classifies up to <paramref name="budget"/> windows of the utterance, pooling each into the
    /// accumulator, and stops early once the decision is committed.
    ///
    /// <para>Windows that are mostly pause are skipped without an inference and do not count against
    /// the budget — silence is exactly what makes a classifier answer confidently and wrongly, and
    /// recognizing that costs nothing. Windows the backend declines <em>do</em> count: the inference
    /// was spent. Either way the stretch is marked examined, so the schedule moves on to audio that
    /// has not been ruled out yet instead of asking the same question again.</para>
    /// </summary>
    /// <returns>True once the language is committed and no further windows are worth running.</returns>
    private bool ExamineWindows(int onsetSample, bool allowOverlap, int budget)
    {
        var evidence = _evidence!;
        var probes = _probes!;

        var spent = 0;
        while (spent < budget)
        {
            var availableMs = SamplesToMs(BufferedCount() - onsetSample);
            if (!probes.TryNext(availableMs, allowOverlap, out var probe)) return false;

            probes.MarkExamined(probe);

            var window = Snapshot(onsetSample + MsToSamples(probe.StartMs), MsToSamples(probe.LengthMs));

            // Measured per frame against this speaker's own level, not as one average over the whole
            // window: a window that is four fifths silence with one loud syllable in it averages out
            // the same as continuous speech, and handing that to the classifier is how a session ends
            // up routed by its background noise.
            var voiced = _onset.VoicedRatioOf(window);
            if (voiced < MinVoicedRatio)
            {
                _windowsSilent++;
                evidence.AddExamined(probe.StartMs, probe.LengthMs);
                Log.Debug("Language window at +{Offset} ms skipped: only {Voiced:P0} of it is speech.",
                    probe.StartMs, voiced);
                continue;
            }

            var observation = _detector.Observe(
                window, _options.Candidates, new ObservationContext(probe.StartMs, voiced));
            spent++;

            if (observation is null)
            {
                // The backend declined — Silero does when the audio is none of the user's languages, or
                // is not speech at all. A throat-clear, a filler, a proper name. Somewhere else in the
                // utterance is a genuinely new question; the same audio again is not.
                _windowsDeclined++;
                Log.Debug("Language window at +{Offset} ms ({Length} ms) declined; scanning further along.",
                    probe.StartMs, probe.LengthMs);
                evidence.AddExamined(probe.StartMs, probe.LengthMs);
                continue;
            }

            _windowsAnswered++;
            var weight = evidence.Add(observation);
            Log.Debug("Language window at +{Offset} ms ({Length} ms, {Novel} ms new) → {Code} {Probability:P0}, " +
                      "weight {Weight:0.00}; now {State}.",
                probe.StartMs, probe.LengthMs, probe.NovelMs,
                observation.Best.Code, observation.Best.Probability, weight, evidence.Describe());

            if (ApplyVerdict(evidence.Evaluate(_target.LanguageCode, _languageCommitted, _languageSwitches)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Acts on the accumulator's recommendation: routes to it when it names a language other than the
    /// one in use, and reports whether the question is now closed.
    /// </summary>
    private bool ApplyVerdict(LanguageVerdict verdict)
    {
        if (verdict.HasLanguage && verdict.Code != _target.LanguageCode)
        {
            // Only a change away from an established language counts as a switch; the first routing
            // decision of a session displaces nothing.
            if (_target.LanguageCode is not null) _languageSwitches++;
            ApplyLanguage(verdict.Code, manual: false);
        }

        _languageCommitted = verdict.IsCommitted && verdict.Code == _target.LanguageCode;
        if (!_languageCommitted) return false;

        _languageSettled = true;
        Log.Information("Language committed: {Description}.", _evidence!.Describe());
        return true;
    }


    /// <summary>
    /// Tells the user once that nothing could be identified, rather than leaving the widget's chip
    /// stuck on "--" with no explanation while dictation quietly carries on with the default model.
    ///
    /// <para>Which of three things went wrong is reported separately, because only one of them is
    /// about language at all. A window can be skipped as silence, examined and found to hold none of
    /// the user's languages, or genuinely weighed and found too close to call. The first two are
    /// capture problems — a muted or wrong input device, a mic left picking up mains hum — and the
    /// user can act on them the moment they are named. Reporting all three as "language unclear" is
    /// what let a dead microphone read as a broken classifier for as long as it did.</para>
    /// </summary>
    private void ReportLanguageUnclear()
    {
        if (_unclearReported || _target.LanguageCode is not null) return;
        _unclearReported = true;

        if (_windowsAnswered == 0 && _windowsDeclined == 0 && _windowsSilent > 0)
        {
            StatusChanged?.Invoke("No speech heard · check the microphone");
            Log.Warning("Language identification found no speech in {Count} window(s) of {Ms} ms. The capture " +
                        "is silent or nearly so — check that the right input device is selected.",
                _windowsSilent, SamplesToMs(BufferedCount()));
            return;
        }

        if (_windowsAnswered == 0 && _windowsDeclined > 0)
        {
            StatusChanged?.Invoke("Audio unreadable · check the microphone");
            Log.Warning("Language identification examined {Count} window(s) and none of them looked like " +
                        "{Languages} — or like speech at all. The capture is most likely noise rather than " +
                        "voice: the wrong input device, or one picking up mains hum.",
                _windowsDeclined, string.Join("/", _options.Candidates));
            return;
        }

        StatusChanged?.Invoke("Language unclear · using default model");
    }

    /// <summary>
    /// The finishing pass, run once the user has stopped speaking: examines whatever the live pass
    /// never got to, then takes the best answer the evidence supports. With
    /// <see cref="SessionOptions.DeferDetectionUntilStop"/> that is the entire utterance, because this
    /// is the only pass there is — which is the point, since every window it spends is then spent on the
    /// whole recording rather than on the first second of it.
    ///
    /// <para>Nothing here is a separate algorithm — that was the old design's mistake. The sweep used
    /// to be its own contest, picking the single most confident of a handful of fresh windows while
    /// ignoring everything the live probes had already learned, and it only ran at all when no language
    /// had been found, so a confident-but-wrong early guess was never revisited. This drains the same
    /// schedule into the same accumulator: windows examined during the utterance still count, the tail
    /// nobody has seen is finally looked at, and the decision is the same decision, better informed.</para>
    ///
    /// <para>The whole recording is in hand by now and no live decode is competing for the CPU, so
    /// windows may overlap examined audio to reach a usable length — at the end of the audio a partly
    /// re-read window is the best offer left, and the accumulator discounts it accordingly.</para>
    ///
    /// <para>A language already committed is left alone: re-routing here costs a model load the user
    /// waits through, and the commit rule exists precisely so that the decision does not need
    /// revisiting.</para>
    /// </summary>
    private void SettleLanguage()
    {
        if (!DetectionPossible || _manualOverride || _languageCommitted) return;
        if (!_detector.IsReady) return;

        EnsureDetectionState();

        // Whatever the live pass measured its windows from, so the coverage built up during the
        // utterance still lines up with the audio it refers to. If detection never got that far, the
        // onset — or the loudest audio, if speech was never confidently located — and failing that the
        // start of the capture, since by now there is nothing left to wait for.
        var total = BufferedCount();
        var start = _detectionOrigin ?? (int)(_onset.OnsetSample ?? _onset.BestGuessOnsetSample);
        if (total - start < MsToSamples(_detector.MinWindowMs) && _detectionOrigin is null) start = 0;
        _detectionOrigin = start;

        // Bounded by the scheduler's own window budget rather than a count here.
        ExamineWindows(start, allowOverlap: true, budget: int.MaxValue);

        // No more audio is coming, so the bar drops from "proven" to "worth acting on": a likely
        // language beats transcribing with a model chosen by default.
        var verdict = _evidence!.Concede(_target.LanguageCode, _languageCommitted, _languageSwitches);
        ApplyVerdict(verdict);
        _languageSettled = true;

        if (verdict.HasLanguage)
        {
            Log.Information("Language settled after the recording ended: {Description}.", _evidence.Describe());
        }
        else
        {
            Log.Debug("Language never resolved across {Ms} ms of audio: {Description}.",
                SamplesToMs(total), _evidence.Describe());
            ReportLanguageUnclear();
        }
    }

    private void ApplyPendingOverride()
    {
        var code = Interlocked.Exchange(ref _pendingOverride, null);
        if (code is null) return;

        _manualOverride = true;
        _languageSettled = true; // the user has spoken; stop second-guessing them
        _languageCommitted = true;
        ApplyLanguage(code == AutoOverride ? null : code, manual: true);
    }

    /// <summary>
    /// Routes to <paramref name="code"/>'s engine/model, or — for a null code — back to the user's
    /// default target with no language pinned. A model change starts a new preload; any language
    /// change discards decoded text, because it was produced by the wrong model.
    /// </summary>
    private void ApplyLanguage(string? code, bool manual)
    {
        // Resolve() would re-attach the hand-picked language when routing is off, which is the
        // opposite of what "back to automatic" asks for, so that case builds its target directly.
        var next = code is null
            ? _router.DefaultTarget() with { LanguageCode = null }
            : _router.Resolve(code);
        var label = code ?? "auto";

        if (!_router.IsInstalled(next))
        {
            // Report what was heard so the widget can still show it, but keep transcribing with
            // the model we actually have rather than failing the session.
            StatusChanged?.Invoke($"{label} → model not downloaded");
            _target = _target with { LanguageCode = code };
            TargetChanged?.Invoke(_target);
            return;
        }

        var languageChanged = next.LanguageCode != _target.LanguageCode;
        var modelChanged = !next.SameModelAs(_target);
        _target = next;
        TargetChanged?.Invoke(next);

        if (!languageChanged && !modelChanged) return;

        Log.Information("Dictation routed to {Code} → {Engine}/{Model}{Manual}.",
            label, next.Engine, next.Model, manual ? " (manual)" : "");

        // Anything decoded so far came from the previous language/model, so it cannot be trusted.
        ResetDecoded();

        if (modelChanged)
        {
            _load = _router.PreloadAsync(next, _cts?.Token ?? CancellationToken.None);
            StatusChanged?.Invoke($"{label} · loading {next.Model}…");
        }
        else
        {
            StatusChanged?.Invoke($"{label} · listening…");
        }
    }

    /// <summary>Throws away all decoded text and rewinds both engines to the start of the audio.</summary>
    private void ResetDecoded()
    {
        _generation++;
        _committed = "";
        _preview = "";
        _hasPinned = false;

        _voskStream?.Dispose();
        _voskStream = null;
        _nemotronStream?.Dispose();
        _nemotronStream = null;

        lock (_bufferGate)
        {
            _windowStart = 0;
            _voskFed = 0;
            _nemotronFed = 0;
        }

        _lastDecodeTick = 0;
        _lastDecodedTail = 0;
        PreviewChanged?.Invoke("");
    }

    // ---- Decoding ----

    private async Task<bool> EnsureModelReadyAsync(CancellationToken ct)
    {
        // A deferred session may have held the opening preload back (see ShouldPreloadInitialTarget),
        // and detection may then have landed on the model it was already pointing at, leaving nothing
        // started. Start it here rather than reporting a load failure for a load nobody asked for yet.
        _load ??= _router.PreloadAsync(_target, ct);

        try { return await _load.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            Log.Error(ex, "Route model {Model} failed to load.", _target.Model);
            return false;
        }
    }

    private async Task DecodeAsync(bool final, CancellationToken ct)
    {
        // A live decode never waits on the load; it simply skips until the model is resident.
        if (!final && !IsCompletedTrue(_load)) return;

        if (_target.IsVosk) DecodeVosk(final);
        else if (_target.IsNemotron) DecodeNemotron(final);
        else if (_target.IsBark) await DecodeBarkAsync(final, ct).ConfigureAwait(false);
        else await DecodeWhisperAsync(final, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Feeds Nemotron everything captured since the last call. Like Vosk this is the engine's
    /// native mode — sherpa-onnx decodes each cache-aware chunk as its audio arrives and reports
    /// an endpoint when a sentence ends — so nothing is ever re-decoded.
    ///
    /// <para>The routed language is handed to the stream when it is created, which is why the
    /// stream is built lazily here rather than in the constructor: by the time the first audio
    /// arrives, detection has usually already settled on a code. A later language change disposes
    /// the stream via <see cref="ResetDecoded"/>, so the next call rebuilds it with the new
    /// prompt.</para>
    /// </summary>
    private void DecodeNemotron(bool final)
    {
        _nemotronStream ??= _nemotron.CreateStream(_target.LanguageCode);

        int from, to;
        lock (_bufferGate)
        {
            from = _nemotronFed;
            to = _buffer.Count;
        }

        if (to > from)
        {
            var chunk = Snapshot(from, to - from);
            foreach (var utterance in _nemotronStream.Feed(chunk)) AppendCommitted(utterance);
            lock (_bufferGate) _nemotronFed = to;
        }

        if (final)
        {
            AppendCommitted(_nemotronStream.Finish());
            SetPreview("");
        }
        else
        {
            SetPreview(_nemotronStream.Partial());
        }
    }

    /// <summary>
    /// Feeds Vosk everything captured since the last call. This is the engine's native mode:
    /// utterances come back finalized as it decides they ended, with a partial hypothesis in
    /// between, so there is no re-decoding at all.
    /// </summary>
    private void DecodeVosk(bool final)
    {
        _voskStream ??= _vosk.CreateStream();

        int from, to;
        lock (_bufferGate)
        {
            from = _voskFed;
            to = _buffer.Count;
        }

        if (to > from)
        {
            var chunk = Snapshot(from, to - from);
            foreach (var utterance in _voskStream.Feed(chunk)) AppendCommitted(utterance);
            lock (_bufferGate) _voskFed = to;
        }

        if (final)
        {
            AppendCommitted(_voskStream.Finish());
            SetPreview("");
        }
        else
        {
            SetPreview(_voskStream.Partial());
        }
    }

    /// <summary>
    /// Re-decodes the uncommitted tail with Whisper and commits whatever has settled, so the
    /// window stays short no matter how long the user talks.
    /// </summary>
    private async Task DecodeWhisperAsync(bool final, CancellationToken ct)
    {
        int windowStart, buffered;
        lock (_bufferGate)
        {
            windowStart = _windowStart;
            buffered = _buffer.Count;
        }

        var tail = buffered - windowStart;
        if (tail <= 0) return;

        if (!final)
        {
            if (SamplesToMs(tail) < MinLiveTailMs) return;
            if (Environment.TickCount64 - _lastDecodeTick < DecodeIntervalMs) return;
            if (SamplesToMs(tail - _lastDecodedTail) < MinNewAudioMs) return;
        }

        EnsureLanguagePinned();

        var window = Snapshot(windowStart, tail);
        var generation = _generation;

        // A language switch cancels the decode in flight, since its output would be discarded.
        using var decodeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _decodeCts = decodeCts;
        try
        {
            var segments = await _whisper
                .TranscribeSegmentsAsync(window, decodeCts.Token)
                .ConfigureAwait(false);

            // Superseded while decoding: the model changed underneath us, so drop the result.
            if (generation != _generation) return;

            var split = RollingCommitPolicy.Split(
                segments, window.Length, _options.SampleRate, RollingCommitPolicy.DefaultLagMs, final);

            if (!string.IsNullOrEmpty(split.CommittedText))
            {
                AppendCommitted(split.CommittedText);
                lock (_bufferGate) _windowStart = windowStart + split.CommittedSamples;
                _lastDecodedTail = 0;
            }
            else
            {
                _lastDecodedTail = tail;
            }

            SetPreview(split.PreviewText);
        }
        finally
        {
            _decodeCts = null;
            _lastDecodeTick = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Re-decodes the uncommitted tail with the Bark (Parakeet-TDT) recognizer.
    ///
    /// <para>Bark is an offline model — one window in, one transcript out, no streaming decoder and
    /// no endpointing — so it is driven exactly like Whisper: the same rolling window, the same
    /// settle-before-you-commit rule, the same generation guard against a model swap landing
    /// mid-decode. The only difference is that there is no language to pin: the multilingual export
    /// detects it per window, and the English-only one has no prompt input at all.</para>
    /// </summary>
    private async Task DecodeBarkAsync(bool final, CancellationToken ct)
    {
        int windowStart, buffered;
        lock (_bufferGate)
        {
            windowStart = _windowStart;
            buffered = _buffer.Count;
        }

        var tail = buffered - windowStart;
        if (tail <= 0) return;

        if (!final)
        {
            if (SamplesToMs(tail) < MinLiveTailMs) return;
            if (Environment.TickCount64 - _lastDecodeTick < DecodeIntervalMs) return;
            if (SamplesToMs(tail - _lastDecodedTail) < MinNewAudioMs) return;
        }

        var window = Snapshot(windowStart, tail);
        var generation = _generation;

        // A model swap cancels the decode in flight, since its output would be discarded.
        using var decodeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _decodeCts = decodeCts;
        try
        {
            var segments = await _bark
                .TranscribeSegmentsAsync(window, decodeCts.Token)
                .ConfigureAwait(false);

            if (generation != _generation) return;

            var split = RollingCommitPolicy.Split(
                segments, window.Length, _options.SampleRate, RollingCommitPolicy.DefaultLagMs, final);

            if (!string.IsNullOrEmpty(split.CommittedText))
            {
                AppendCommitted(split.CommittedText);
                lock (_bufferGate) _windowStart = windowStart + split.CommittedSamples;
                _lastDecodedTail = 0;
            }
            else
            {
                _lastDecodedTail = tail;
            }

            SetPreview(split.PreviewText);
        }
        finally
        {
            _decodeCts = null;
            _lastDecodeTick = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Tells Whisper which language it is hearing once routing knows, so it stops re-detecting on
    /// every rolling decode — cheaper, and it cannot drift to a different language mid-utterance.
    /// Runs on the pump, so it never collides with a decode.
    /// </summary>
    private void EnsureLanguagePinned()
    {
        var want = _target.LanguageCode;
        if (_hasPinned && _pinnedLanguage == want) return;

        _whisper.PinLanguage(want);
        _pinnedLanguage = want;
        _hasPinned = true;
    }

    // ---- Text plumbing ----

    private void AppendCommitted(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        _committed = _committed.Length == 0 ? trimmed : _committed + " " + trimmed;
        RaisePreview();
    }

    private void SetPreview(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed == _preview) return;

        _preview = trimmed;
        RaisePreview();
    }

    private void RaisePreview()
    {
        var text = _preview.Length == 0
            ? _committed
            : (_committed.Length == 0 ? _preview : _committed + " " + _preview);
        PreviewChanged?.Invoke(text);
    }

    // ---- Buffer helpers ----

    private int BufferedCount()
    {
        lock (_bufferGate) return _buffer.Count;
    }

    /// <summary>Copies up to <paramref name="count"/> samples starting at <paramref name="start"/>.</summary>
    private float[] Snapshot(int start, int count)
    {
        lock (_bufferGate)
        {
            var available = Math.Max(0, Math.Min(count, _buffer.Count - start));
            var copy = new float[available];
            if (available > 0) _buffer.CopyTo(start, copy, 0, available);
            return copy;
        }
    }

    private int MsToSamples(int ms) => _options.SampleRate * ms / 1000;

    private int SamplesToMs(int samples) => samples <= 0 ? 0 : samples * 1000 / _options.SampleRate;

    private static bool IsCompletedTrue(Task<bool>? task) =>
        task is { IsCompletedSuccessfully: true } && task.Result;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts?.Cancel(); } catch { /* ignore */ }

        // The pump may still be inside a native decoder (VoskRecognizer.AcceptWaveform,
        // OnlineRecognizer.Decode, Whisper's ProcessAsync). Freeing the stream — or the wait handle
        // it is blocked on — from here would be a use-after-free: the decoder keeps running on that
        // pointer and the process dies with an AccessViolationException and no managed stack.
        // So the native teardown is handed to the pump: it runs the moment the decode loop returns.
        var pump = _pump;
        if (pump is { IsCompleted: false })
        {
            pump.ContinueWith(
                _ => DisposeNativeResources(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        DisposeNativeResources();
    }

    /// <summary>
    /// Frees the native streams. Only safe once <see cref="_pump"/> has returned — see
    /// <see cref="Dispose"/>. Idempotent, because either the caller or the pump continuation runs it.
    /// </summary>
    private void DisposeNativeResources()
    {
        try { _voskStream?.Dispose(); } catch { /* ignore */ }
        _voskStream = null;
        try { _nemotronStream?.Dispose(); } catch { /* ignore */ }
        _nemotronStream = null;
        try { _cts?.Dispose(); } catch { /* ignore */ }
        try { _signal.Dispose(); } catch { /* ignore */ }
    }
}
