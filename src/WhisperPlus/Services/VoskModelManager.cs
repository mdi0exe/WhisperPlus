using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Manages Vosk model folders on disk: download the zip (with progress), unpack it, check
/// what is installed, report sizes, and delete. Vosk models are archives rather than single
/// files, so a download has two phases — transfer, then extract — and both have to be
/// crash-safe: the zip streams to a <c>.part</c> file and the archive unpacks into a staging
/// folder that is only renamed into place once complete. An interrupted download therefore
/// never leaves a half-unpacked folder that Vosk would try to load.
/// </summary>
public sealed class VoskModelManager : IDisposable
{
    /// <summary>Subfolder of the shared models directory that holds unpacked Vosk models.</summary>
    private const string SubFolder = "vosk";

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;

    public VoskModelManager(ISettingsService settings)
    {
        _settings = settings;
        // Longer timeout: the large models are well over a gigabyte.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
    }

    /// <summary>Folder that holds one subfolder per installed Vosk model.</summary>
    public string ModelsDirectory => Path.Combine(_settings.Current.WhisperModelsDirectory, SubFolder);

    /// <summary>Full path a model would occupy on disk (whether or not it exists).</summary>
    public string PathFor(string id) => Path.Combine(ModelsDirectory, id);

    /// <summary>
    /// True if the model folder for <paramref name="id"/> looks like a usable Vosk model.
    /// Every model ships a <c>conf/</c> (or at least an <c>am/</c>) subfolder, so their
    /// presence distinguishes a finished unpack from a stray or partial directory.
    /// </summary>
    public bool IsInstalled(string id)
    {
        try
        {
            var dir = PathFor(id);
            return Directory.Exists(Path.Combine(dir, "conf"))
                || Directory.Exists(Path.Combine(dir, "am"));
        }
        catch { return false; }
    }

    /// <summary>Returns the on-disk size in MB of an installed model, or 0 if not installed.</summary>
    public long GetInstalledSizeMb(string id)
    {
        try
        {
            var dir = PathFor(id);
            if (!Directory.Exists(dir)) return 0;
            var bytes = new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
            return bytes / (1024 * 1024);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Downloads and unpacks a model. <paramref name="progress"/> reports transfer percent
    /// (0–100) and <paramref name="status"/> reports the current phase for display.
    /// Returns the installed folder path. Cancellation propagates as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<string> DownloadAsync(
        string id,
        IProgress<double>? progress = null,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        _ = VoskModelCatalog.Find(id)
            ?? throw new ArgumentException($"Unknown Vosk model id: {id}", nameof(id));

        Directory.CreateDirectory(ModelsDirectory);
        var finalDir = PathFor(id);

        if (IsInstalled(id))
        {
            Log.Information("Vosk model {Id} already present at {Path}; skipping download.", id, finalDir);
            return finalDir;
        }

        var zipPath = Path.Combine(ModelsDirectory, id + ".zip");
        var partPath = zipPath + ".part";
        var stagingDir = Path.Combine(ModelsDirectory, "." + id + ".staging");
        var url = VoskModelCatalog.UrlFor(id);
        Log.Information("Downloading Vosk model {Id} from {Url}", id, url);

        try
        {
            status?.Report($"Downloading {id}…");
            await DownloadZipAsync(url, partPath, progress, ct);

            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(partPath, zipPath);

            status?.Report($"Extracting {id}… this can take a minute.");
            await Task.Run(() => Extract(zipPath, stagingDir, finalDir), ct);

            File.Delete(zipPath);
            Log.Information("Vosk model {Id} installed at {Path}.", id, finalDir);
            status?.Report($"{id} ready.");
            return finalDir;
        }
        catch (OperationCanceledException)
        {
            Cleanup(partPath, zipPath, stagingDir);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Vosk model download failed for {Id}.", id);
            Cleanup(partPath, zipPath, stagingDir);
            throw;
        }
    }

    /// <summary>Streams the archive to <paramref name="partPath"/>, reporting percent read.</summary>
    private async Task DownloadZipAsync(string url, string partPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        long readBytes = 0;
        var lastReport = DateTime.MinValue;

        await using var content = await response.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

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
                progress?.Report(readBytes * 100.0 / totalBytes);
            }
        }

        progress?.Report(100);
    }

    /// <summary>
    /// Unpacks the archive into a staging folder, then moves the model root into place.
    /// The archives wrap everything in a single top-level directory whose name usually — but
    /// not always — matches the zip, so the root is detected rather than assumed.
    /// </summary>
    private static void Extract(string zipPath, string stagingDir, string finalDir)
    {
        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, stagingDir);

        var root = stagingDir;
        var entries = Directory.GetFileSystemEntries(stagingDir);
        if (entries.Length == 1 && Directory.Exists(entries[0])) root = entries[0];

        if (Directory.Exists(finalDir)) Directory.Delete(finalDir, recursive: true);
        Directory.Move(root, finalDir);

        if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
    }

    /// <summary>Deletes an installed model folder. No-op if not present.</summary>
    public void Delete(string id)
    {
        try
        {
            var dir = PathFor(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Cleanup(Path.Combine(ModelsDirectory, id + ".zip.part"),
                    Path.Combine(ModelsDirectory, id + ".zip"),
                    Path.Combine(ModelsDirectory, "." + id + ".staging"));
            Log.Information("Deleted Vosk model {Id}.", id);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not delete Vosk model {Id}.", id);
        }
    }

    private static void Cleanup(string partPath, string zipPath, string stagingDir)
    {
        try { if (File.Exists(partPath)) File.Delete(partPath); } catch { /* ignore */ }
        try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* ignore */ }
        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { /* ignore */ }
    }

    public void Dispose() => _http.Dispose();
}
