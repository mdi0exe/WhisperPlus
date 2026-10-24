using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// The app's entry point for "what language is being spoken?". Picks the backend the user
/// configured, and quietly falls back to the other one when the configured model is missing, so a
/// user who has not downloaded the Silero classifier still gets routing from the small Whisper
/// model they already have.
///
/// <para>Backend selection is resolved per session (at <see cref="EnsureReadyAsync"/>) rather than
/// per call, so a session cannot end up with its first guess from one model and its confirming
/// guess from another — which would make a disagreement between them indistinguishable from the
/// speaker actually switching language.</para>
/// </summary>
public sealed class LanguageDetector : IDisposable
{
    private readonly SileroLanguageIdentifier _silero;
    private readonly WhisperLanguageIdentifier _whisper;
    private readonly ISettingsService _settings;
    private readonly object _gate = new();

    private ILanguageIdentifier? _active;
    private bool _disposed;

    public LanguageDetector(
        SileroLanguageIdentifier silero,
        WhisperLanguageIdentifier whisper,
        ISettingsService settings)
    {
        _silero = silero;
        _whisper = whisper;
        _settings = settings;
    }

    /// <summary>The configured backend id, before any fallback is applied.</summary>
    public string Backend => LanguageDetectorBackends.Normalize(_settings.Current.LanguageDetectorBackend);

    /// <summary>
    /// The backend that will actually be used: the configured one when its model is on disk,
    /// otherwise the other one. Never null — the caller checks <see cref="IsDetectorInstalled"/> to
    /// learn whether *either* is usable.
    /// </summary>
    private ILanguageIdentifier Preferred
    {
        get
        {
            var configured = Backend == LanguageDetectorBackends.Whisper
                ? (ILanguageIdentifier)_whisper
                : _silero;
            if (configured.IsInstalled) return configured;

            var other = ReferenceEquals(configured, _silero)
                ? (ILanguageIdentifier)_whisper
                : _silero;
            return other.IsInstalled ? other : configured;
        }
    }

    /// <summary>
    /// A short label naming the model that will do the identifying — used in the widget's status
    /// line, so the user can tell which backend is in play and whether it needs downloading.
    /// </summary>
    public string DetectorModelId
    {
        get
        {
            lock (_gate) { if (_active is not null) return _active.Name; }
            return Preferred.Name;
        }
    }

    /// <summary>
    /// Whether some backend can identify a language right now. False means routing degrades to the
    /// user's default engine, which callers surface rather than stalling dictation.
    /// </summary>
    public bool IsDetectorInstalled => _silero.IsInstalled || _whisper.IsInstalled;

    /// <summary>True when a backend is loaded and ready to answer.</summary>
    public bool IsReady
    {
        get { lock (_gate) return _active?.IsReady == true; }
    }

    /// <summary>
    /// Shortest window the active backend wants. Falls back to the preferred backend's value before
    /// a session has resolved one, so a caller can size its first window without waiting on a load.
    /// </summary>
    public int MinWindowMs
    {
        get { lock (_gate) return (_active ?? Preferred).MinWindowMs; }
    }

    /// <summary>Longest window worth handing the active backend.</summary>
    public int MaxWindowMs
    {
        get { lock (_gate) return (_active ?? Preferred).MaxWindowMs; }
    }

    /// <summary>
    /// Resolves and loads a backend on a background thread. Concurrent calls share one load.
    /// Returns false when no backend has a model on disk, in which case the caller should carry on
    /// without routing.
    /// </summary>
    public Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        ILanguageIdentifier chosen;
        lock (_gate)
        {
            // Once a session has settled on a backend, stay with it.
            chosen = _active ?? Preferred;
            if (!chosen.IsInstalled)
            {
                Log.Warning("No language-ID model is installed ({Backend} was requested).", Backend);
                return Task.FromResult(false);
            }
            _active = chosen;
        }

        return chosen.EnsureReadyAsync(ct);
    }

    /// <summary>
    /// The backend that is actually answering, as a stable id — what the accumulator's prior keys its
    /// bias correction on, since Whisper's language head and Silero's classifier are wrong in
    /// different directions and want different corrections.
    /// </summary>
    public string ActiveBackend
    {
        get
        {
            ILanguageIdentifier? active;
            lock (_gate) active = _active;
            return ReferenceEquals(active ?? Preferred, _whisper)
                ? LanguageDetectorBackends.Whisper
                : LanguageDetectorBackends.Silero;
        }
    }

    /// <summary>
    /// Scores <paramref name="candidates"/> against one window of audio. <paramref name="samples"/>
    /// must already start at speech — see <see cref="SpeechOnsetDetector"/>; handing this the silence
    /// at the top of a capture is what made every session look like English.
    ///
    /// <para>Returns the whole distribution plus the signals that say how much it is worth, because
    /// one window is never the decision — <see cref="LanguageEvidenceAccumulator"/> pools several.</para>
    ///
    /// <para>Blocking and CPU-bound — call it from a background thread.</para>
    /// </summary>
    public LanguageObservation? Observe(
        float[] samples,
        IReadOnlyList<string> candidates,
        ObservationContext context)
    {
        ILanguageIdentifier? active;
        lock (_gate) active = _active;
        return active?.Observe(samples, candidates, context);
    }

    /// <summary>
    /// Identifies which of <paramref name="candidates"/> is being spoken in a single window, for a
    /// caller with no utterance to accumulate over.
    /// </summary>
    public LanguageGuess? Detect(float[] samples, IReadOnlyList<string> candidates) =>
        Observe(samples, candidates, ObservationContext.Unknown)?.Best;

    /// <summary>
    /// Releases whichever backend is loaded and forgets the choice, so the next session re-resolves
    /// it — picking up a model the user downloaded in the meantime.
    /// </summary>
    public void Unload()
    {
        ILanguageIdentifier? active;
        lock (_gate)
        {
            active = _active;
            _active = null;
        }
        active?.Unload();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _silero.Dispose();
        _whisper.Dispose();
    }
}
