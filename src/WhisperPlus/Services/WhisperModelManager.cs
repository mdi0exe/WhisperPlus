using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Manages Whisper GGML model files on disk: download (with progress), existence checks,
/// deletion, and path resolution. Downloads stream directly to a <c>.part</c> temp file
/// and are atomically renamed on completion, so an interrupted download never leaves a
/// half-written model that Whisper could mistake for valid.
/// </summary>
public sealed class WhisperModelManager : IDisposable
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;

    /// <summary>Per-download progress: 0..100. Raised on a background thread.</summary>
    public event Action<string, double>? DownloadProgress;

    /// <summary>Raised when a model download finishes (successfully or not).</summary>
    public event Action<string, bool>? DownloadCompleted;

    public WhisperModelManager(ISettingsService settings)
    {
        _settings = settings;
        // Longer timeouts for the multi-hundred-MB model files.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <summary>Folder where GGML model files live.</summary>
    public string ModelsDirectory => _settings.Current.WhisperModelsDirectory;

    /// <summary>Full path a model id would occupy on disk (whether or not it exists).</summary>
    public string PathFor(string id) => Path.Combine(ModelsDirectory, WhisperModelCatalog.FileNameFor(id));

    /// <summary>True if the model file for <paramref name="id"/> exists and is non-empty.</summary>
    public bool IsInstalled(string id)
    {
        try
        {
            var path = PathFor(id);
            return File.Exists(path) && new FileInfo(path).Length > 1024; // sanity floor
        }
        catch { return false; }
    }

    /// <summary>Returns the size in MB of an installed model, or 0 if not installed.</summary>
    public long GetInstalledSizeMb(string id)
    {
        try
        {
            var path = PathFor(id);
            return File.Exists(path) ? new FileInfo(path).Length / (1024 * 1024) : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Downloads a model to disk with progress reporting. Streams to <c>.part</c> then
    /// renames. Returns the final path on success. Cancellation propagates as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<string> DownloadAsync(string id, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var info = WhisperModelCatalog.Find(id)
            ?? throw new ArgumentException($"Unknown model id: {id}", nameof(id));

        Directory.CreateDirectory(ModelsDirectory);
        var finalPath = PathFor(id);
        var partPath = finalPath + ".part";

        // If a complete file already exists, nothing to do.
        if (IsInstalled(id))
        {
            Log.Information("Model {Id} already present at {Path}; skipping download.", id, finalPath);
            DownloadCompleted?.Invoke(id, true);
            return finalPath;
        }

        var url = WhisperModelCatalog.UrlFor(id);
        Log.Information("Downloading model {Id} from {Url}", id, url);

        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            long readBytes = 0;
            var lastReport = DateTime.MinValue;

            await using (var content = await response.Content.ReadAsStreamAsync(ct))
            await using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await content.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    readBytes += n;

                    // Throttle progress reporting to ~10/sec so we don't flood the UI thread.
                    if (totalBytes > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                    {
                        lastReport = DateTime.UtcNow;
                        var pct = readBytes * 100.0 / totalBytes;
                        progress?.Report(pct);
                        DownloadProgress?.Invoke(id, pct);
                    }
                }
            }

            // Atomic move into place (overwrite any stale complete file).
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partPath, finalPath);

            progress?.Report(100);
            DownloadProgress?.Invoke(id, 100);
            DownloadCompleted?.Invoke(id, true);

            Log.Information("Model {Id} downloaded ({Mb} MB).", id, readBytes / (1024 * 1024));
            return finalPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            DownloadCompleted?.Invoke(id, false);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Model download failed for {Id}.", id);
            TryDelete(partPath);
            DownloadCompleted?.Invoke(id, false);
            throw;
        }
    }

    /// <summary>Deletes a model file from disk. No-op if not present.</summary>
    public void Delete(string id)
    {
        try
        {
            var path = PathFor(id);
            if (File.Exists(path)) File.Delete(path);
            TryDelete(path + ".part");
            Log.Information("Deleted model {Id}.", id);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not delete model {Id}.", id);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* ignore */ }
    }

    public void Dispose() => _http.Dispose();
}
