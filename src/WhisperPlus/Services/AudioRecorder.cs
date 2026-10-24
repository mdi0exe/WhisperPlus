using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.MediaFoundation;
using NAudio.Wave;
using Serilog;

namespace WhisperPlus.Services;

/// <summary>Recording lifecycle states shown by the UI.</summary>
public enum RecordingState
{
    Idle,
    Recording,
    Paused,
    Stopping,
}

/// <summary>
/// NAudio-backed audio recorder. Captures from a WaveIn device (16-bit, 44.1kHz, mono
/// by default for compact files), writes to a WAV file directly during capture, and
/// optionally re-encodes to MP3 on stop. Exposes live level samples through a
/// <see cref="WaveformAggregator"/> for the visualizer.
///
/// <para>Pause/Resume is implemented by muting the writer (dropping buffers) rather than
/// stopping and restarting the capture device — this avoids the latency/device-reopen
/// cost and produces a single contiguous file with the paused section removed.</para>
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private const int SampleRate = 44100;
    private const int Channels = 1;

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _currentFilePath;
    private readonly object _gate = new();

    /// <summary>
    /// The container the user asked for. Capture is always WAV — that is what the device writes —
    /// and an MP3 request is transcoded once the file is closed.
    /// </summary>
    private string _requestedFormat = "wav";

    /// <summary>The aggregator consuming live level samples for the waveform UI.</summary>
    public WaveformAggregator Levels { get; } = new();

    /// <summary>Current lifecycle state. Raised on the UI thread via <see cref="StateChanged"/>.</summary>
    public RecordingState State
    {
        get => (RecordingState)Volatile.Read(ref _state);
        private set => Interlocked.Exchange(ref _state, (int)value);
    }
    private int _state = (int)RecordingState.Idle;

    /// <summary>Raised whenever the state changes (Recording → Paused → Stopped, etc.).</summary>
    public event Action<RecordingState>? StateChanged;

    /// <summary>Raised when a recording finishes, with the path of the saved file.</summary>
    public event Action<string>? RecordingSaved;

    /// <summary>
    /// Raised instead of <see cref="RecordingSaved"/> when a recording finishes that was never
    /// written to disk, because saving was turned off. A separate event rather than a null path so
    /// a subscriber cannot mistake "discarded" for "saved somewhere I didn't catch".
    /// </summary>
    public event Action? RecordingDiscarded;

    /// <summary>
    /// Raised when a recording is abandoned through <see cref="Cancel"/> — the user closed the
    /// widget rather than stopping it. Distinct from <see cref="RecordingDiscarded"/>, which
    /// reports a normally finished recording that saving was turned off for: this one means the
    /// work was thrown away mid-flight, so the UI should simply disappear rather than report.
    /// </summary>
    public event Action? RecordingCancelled;

    /// <summary>Set by <see cref="Cancel"/>; consumed once by the stop handler.</summary>
    private bool _discardOnStop;

    private void SetState(RecordingState value)
    {
        State = value;
        StateChanged?.Invoke(value);
    }

    /// <summary>
    /// Begins capturing from <paramref name="deviceIndex"/> (-1 = system default) into a
    /// timestamped file in <paramref name="directory"/>. Throws if already recording.
    /// </summary>
    /// <param name="save">
    /// When false, nothing is written to disk: no file is created, no directory is touched, and
    /// <see cref="RecordingDiscarded"/> is raised instead of <see cref="RecordingSaved"/> on stop.
    /// The capture device, waveform and pause/resume behaviour are unchanged.
    /// </param>
    public void Start(int deviceIndex, string directory, string format, bool save = true)
    {
        lock (_gate)
        {
            if (State == RecordingState.Recording || State == RecordingState.Paused)
            {
                Log.Warning("Start ignored: recorder already active.");
                return;
            }

            _currentFilePath = null;
            _discardOnStop = false;
            if (save)
            {
                Directory.CreateDirectory(directory);
                // The capture stream is 16-bit PCM, so the file on disk is always a WAV; asking for
                // MP3 only changes what happens after the file is closed. Naming it ".mp3" up front
                // (as this used to) produced a WAV with an MP3 extension that no player would open.
                _requestedFormat = NormalizeFormat(format);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                _currentFilePath = Path.Combine(directory, $"WhisperPlus_{stamp}.wav");
            }

            try
            {
                // No writer at all when saving is off — the buffers are simply not written
                // anywhere, rather than written and deleted later.
                _writer = _currentFilePath is null
                    ? null
                    : new WaveFileWriter(_currentFilePath, new WaveFormat(SampleRate, Channels));

                _waveIn = new WaveInEvent
                {
                    DeviceNumber = AudioDeviceService.ResolveDeviceNumber(deviceIndex),
                    WaveFormat = new WaveFormat(SampleRate, Channels),
                    BufferMilliseconds = 50,
                };
                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.RecordingStopped += OnRecordingStopped;

                Levels.Configure(SampleRate, Channels);
                Levels.SetEnabled(true);
                Levels.Reset();

                _waveIn.StartRecording();
                SetState(RecordingState.Recording);
                Log.Information("Recording started → {Path}", _currentFilePath ?? "(not saved)");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start recording.");
                Cleanup();
                SetState(RecordingState.Idle);
                throw;
            }
        }
    }

    /// <summary>Pauses capture: the device keeps running but buffers are dropped (no file growth).</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (State != RecordingState.Recording) return;
            Levels.SetEnabled(false);
            SetState(RecordingState.Paused);
            Log.Information("Recording paused.");
        }
    }

    /// <summary>Resumes a paused recording.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (State != RecordingState.Paused) return;
            Levels.SetEnabled(true);
            SetState(RecordingState.Recording);
            Log.Information("Recording resumed.");
        }
    }

    /// <summary>Stops capture, flushes/closes the WAV, and raises <see cref="RecordingSaved"/>.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (State == RecordingState.Idle || State == RecordingState.Stopping) return;
            SetState(RecordingState.Stopping);
            Levels.SetEnabled(false);
            try { _waveIn?.StopRecording(); }
            catch (Exception ex) { Log.Warning(ex, "Error stopping WaveIn."); }
        }
    }

    /// <summary>
    /// Abandons the recording: capture stops, the partially written file is closed and deleted,
    /// and <see cref="RecordingCancelled"/> is raised instead of <see cref="RecordingSaved"/>.
    /// This is the "never mind" path behind the widget's close button — nothing captured so far
    /// is kept anywhere. Safe to call while a stop is already in flight, in which case the file
    /// that stop was about to finish is the one deleted.
    /// </summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (State == RecordingState.Idle) return;
            _discardOnStop = true;
        }
        Stop();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)    {
        // Skip writing while paused, but do not stop the capture device.
        var shouldWrite = State == RecordingState.Recording;
        if (shouldWrite)
        {
            try
            {
                _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                Levels.Process(new ReadOnlySpan<byte>(e.Buffer, 0, e.BytesRecorded));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error writing audio buffer.");
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        var savedPath = _currentFilePath;
        var discard = _discardOnStop;
        _discardOnStop = false;
        if (e.Exception is not null)
        {
            Log.Error(e.Exception, "Recording stopped with an error.");
        }

        try { _writer?.Flush(); } catch { /* ignore */ }
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _currentFilePath = null;
        Levels.SetEnabled(false);

        Cleanup();

        if (discard)
        {
            SetState(RecordingState.Idle);
            // The writer is closed by now, so the half-written file can be removed.
            if (savedPath is not null)
            {
                try { File.Delete(savedPath); }
                catch (Exception ex) { Log.Warning(ex, "Could not delete the cancelled recording {Path}", savedPath); }
            }
            Log.Information("Recording cancelled; nothing kept.");
            RecordingCancelled?.Invoke();
            return;
        }

        if (savedPath is null)
        {
            SetState(RecordingState.Idle);
            Log.Information("Recording finished; not saved (saving is turned off).");
            RecordingDiscarded?.Invoke();
            return;
        }

        if (!File.Exists(savedPath))
        {
            SetState(RecordingState.Idle);
            return;
        }

        if (_requestedFormat == "mp3")
        {
            // Transcoding a long recording takes a moment, and this runs on the capture callback
            // thread: hand it off so the device teardown is not held up. Keep state in Stopping/Saving
            // until transcoding finishes so the UI stays truthful and avoids overlapping recordings.
            var wavPath = savedPath;
            _ = Task.Run(() => TranscodeToMp3(wavPath));
            return;
        }

        SetState(RecordingState.Idle);
        Log.Information("Recording saved → {Path}", savedPath);
        RecordingSaved?.Invoke(savedPath);
    }

    /// <summary>The only containers offered; anything else falls back to WAV.</summary>
    private static string NormalizeFormat(string? format) =>
        string.Equals(format?.Trim(), "mp3", StringComparison.OrdinalIgnoreCase) ? "mp3" : "wav";

    /// <summary>
    /// Converts the finished WAV to MP3 with Media Foundation (no bundled encoder) and removes the
    /// WAV. If the encoder is unavailable the WAV is kept and reported instead of failing silently,
    /// so the user always ends up with a playable file.
    /// </summary>
    private void TranscodeToMp3(string wavPath)
    {
        var mp3Path = Path.ChangeExtension(wavPath, "mp3");
        try
        {
            using (var reader = new WaveFileReader(wavPath))
            {
                // 128 kbps mono is plenty for speech and keeps the file small.
                MediaFoundationEncoder.EncodeToMp3(reader, mp3Path, 128_000);
            }

            if (!File.Exists(mp3Path) || new FileInfo(mp3Path).Length == 0)
                throw new InvalidOperationException("The MP3 encoder produced no output.");

            try { File.Delete(wavPath); }
            catch (Exception ex) { Log.Warning(ex, "Could not delete the intermediate WAV {Path}", wavPath); }

            Log.Information("Recording saved → {Path} (MP3)", mp3Path);
            RecordingSaved?.Invoke(mp3Path);
        }
        catch (Exception ex)
        {
            // Keep the WAV: a WAV the user can play beats an MP3 that does not exist.
            Log.Error(ex, "MP3 encoding failed; keeping the WAV {Path}", wavPath);
            try { if (File.Exists(mp3Path)) File.Delete(mp3Path); } catch { /* ignore */ }
            RecordingSaved?.Invoke(wavPath);
        }
        finally
        {
            SetState(RecordingState.Idle);
        }
    }

    private void Cleanup()
    {
        try { _waveIn?.Dispose(); } catch { /* ignore */ }
        _waveIn = null;
    }

    public void Dispose()
    {
        Stop();
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _waveIn?.Dispose(); } catch { /* ignore */ }
    }
}
