using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace WhisperPlus.Services;

/// <summary>
/// Guarantees one running copy of the app per user session.
///
/// <para>The app lives in the tray and owns global resources — registered hotkeys, the low-level
/// screenshot hook, the tray icon, the settings file. A second process holding all of those at once
/// produces duplicate tray icons, conflicting hotkeys and torn settings writes, so a later launch
/// forwards its command line to the running instance over a named pipe and exits instead.</para>
///
/// <para>The primary use is <c>WhisperPlus.exe --capture</c> from an external launcher: it must open
/// the overlay on the instance that already exists, not start a new one.</para>
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    /// <summary>Session-local, so two users on the same machine each get their own instance.</summary>
    private const string MutexName = @"Local\WhisperPlus.SingleInstance";
    private const string PipeName = "WhisperPlus.SingleInstance.Pipe";

    private Mutex? _mutex;
    private bool _hasHandle;
    private CancellationTokenSource? _cts;
    private Task? _listener;

    /// <summary>Raised with the forwarded command line (e.g. "capture") when another instance asks.</summary>
    public event Action<string>? CommandReceived;

    /// <summary>
    /// Claims the instance mutex. Returns true when this process is the first (and should keep
    /// starting up); false when another instance is already running.
    /// </summary>
    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
            if (created)
            {
                _hasHandle = true;
            }
            else
            {
                // Mutex already exists; try to claim it in case the previous owner terminated without releasing it.
                try
                {
                    _hasHandle = _mutex.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    _hasHandle = true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create or acquire single-instance mutex.");
        }

        if (!_hasHandle) return false;

        _cts = new CancellationTokenSource();
        _listener = ListenAsync(_cts.Token);
        return true;
    }

    /// <summary>
    /// Sends this process's command line to the running instance. Returns false when nobody is
    /// listening (the primary is mid-shutdown or just starting).
    /// </summary>
    public static bool TryForward(string[] args)
    {
        // Nothing to ask for when there is no command line at all: showing the settings window is
        // what a plain double-click should do anyway, so it is sent explicitly.
        var payload = args is { Length: > 0 } ? string.Join(' ', args) : "show";

        // Try connecting with brief retries in case the primary is still starting its pipe listener
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1000);

                using (var writer = new StreamWriter(client) { AutoFlush = true })
                    writer.WriteLine(payload);

                Log.Information("Forwarded the launch request to the running instance: '{Args}'", payload);
                return true;
            }
            catch (TimeoutException)
            {
                if (attempt == 2) return false;
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                if (attempt == 2) return false;
                Thread.Sleep(100);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Accepts one forwarded command at a time; each connection carries one line.</summary>
    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(command))
                    CommandReceived?.Invoke(command);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Debug(ex, "The single-instance pipe dropped a connection.");
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        _cts?.Dispose();

        // ReleaseMutex throws if this thread does not own it, which happens exactly when another
        // instance was already running; that path never installs the listener either way.
        if (_hasHandle && _mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        }
        _mutex?.Dispose();
        _mutex = null;
        _hasHandle = false;
    }
}
