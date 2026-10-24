using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Manages Nemotron model folders on disk: download the four sherpa-onnx files (with progress),
/// check what is installed, report sizes, and delete. Unlike Vosk there is no archive to unpack —
/// the files are fetched individually from HuggingFace — but the same crash-safety rule applies:
/// each file streams to a <c>.part</c> and is renamed on completion, and the folder only counts as
/// installed once *all four* files are present. A download interrupted after the 15 MB decoder but
/// before the 650 MB encoder therefore reads as "not installed" and starts over, rather than
/// handing sherpa-onnx a bundle it would fail to open.
/// </summary>
public sealed class NemotronModelManager : IDisposable
{
    /// <summary>Subfolder of the shared models directory that holds Nemotron model folders.</summary>
    private const string SubFolder = "nemotron";

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;

    public NemotronModelManager(ISettingsService settings)
    {
        _settings = settings;
        // Longer timeout: the encoder alone is ~650 MB.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
    }

    /// <summary>Folder that holds one subfolder per installed Nemotron model.</summary>
    public string ModelsDirectory => Path.Combine(_settings.Current.WhisperModelsDirectory, SubFolder);

    /// <summary>Full path a model would occupy on disk (whether or not it exists).</summary>
    public string PathFor(string id) => Path.Combine(ModelsDirectory, id);

    /// <summary>Full path to one file inside a model folder.</summary>
    public string FilePathFor(string id, string fileName) => Path.Combine(PathFor(id), fileName);

    /// <summary>
    /// True only when every file of the bundle is present and non-trivial in size. sherpa-onnx
    /// needs all four, so a partial folder is worse than none — it would fail at load time
    /// instead of at download time.
    /// </summary>
    public bool IsInstalled(string id)
    {
        try
        {
            foreach (var file in NemotronModelCatalog.Files)
            {
                var path = FilePathFor(id, file);
                if (!File.Exists(path) || new FileInfo(path).Length <= 1024) return false;
            }
            return true;
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
    /// Downloads every file of a model. <paramref name="progress"/> reports overall percent
    /// (0–100) across the whole bundle rather than per file, so the bar moves once from empty to
    /// full; <paramref name="status"/> reports which file is in flight. Returns the installed
    /// folder path. Cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<string> DownloadAsync(
        string id,
        IProgress<double>? progress = null,
        IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        var info = NemotronModelCatalog.Find(id)
            ?? throw new ArgumentException($"Unknown Nemotron model id: {id}", nameof(id));

        var finalDir = PathFor(id);
        if (IsInstalled(id))
        {
            Log.Information("Nemotron model {Id} already present at {Path}; skipping download.", id, finalDir);
            return finalDir;
        }

        Directory.CreateDirectory(finalDir);
        Log.Information("Downloading Nemotron model {Id} from {Repo}", id, info.Repository);

        // Weight each file's contribution to the overall bar by its share of the total transfer,
        // so the 650 MB encoder isn't reported as one quarter of the work alongside a 128 KB
        // token list. Sizes come from the server; if a HEAD is refused the file falls back to an
        // equal share, which is only ever a cosmetic inaccuracy.
        var sizes = new long[NemotronModelCatalog.Files.Count];
        for (var i = 0; i < NemotronModelCatalog.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            sizes[i] = await TryGetContentLengthAsync(
                NemotronModelCatalog.UrlFor(info.Repository, NemotronModelCatalog.Files[i]), ct);
        }
        var total = sizes.Sum();
        if (total <= 0) for (var i = 0; i < sizes.Length; i++) { sizes[i] = 1; total = sizes.Length; }

        long completedBytes = 0;
        try
        {
            for (var i = 0; i < NemotronModelCatalog.Files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var file = NemotronModelCatalog.Files[i];
                var finalPath = FilePathFor(id, file);
                var partPath = finalPath + ".part";

                // A retry after a failure part-way through the bundle keeps whatever finished.
                if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 1024)
                {
                    completedBytes += sizes[i];
                    progress?.Report(completedBytes * 100.0 / total);
                    continue;
                }

                status?.Report($"Downloading {file} ({i + 1}/{NemotronModelCatalog.Files.Count})…");

                var done = completedBytes;
                var share = sizes[i];
                var fileProgress = new Progress<double>(pct =>
                    progress?.Report((done + share * pct / 100.0) * 100.0 / total));

                await DownloadFileAsync(
                    NemotronModelCatalog.UrlFor(info.Repository, file), partPath, fileProgress, ct);

                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(partPath, finalPath);

                completedBytes += share;
                progress?.Report(completedBytes * 100.0 / total);
            }

            progress?.Report(100);
            status?.Report($"{id} ready.");
            Log.Information("Nemotron model {Id} installed at {Path}.", id, finalDir);
            return finalDir;
        }
        catch (OperationCanceledException)
        {
            CleanupParts(id);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Nemotron model download failed for {Id}.", id);
            CleanupParts(id);
            throw;
        }
    }

    /// <summary>
    /// Asks the server how big a file is, for weighting the overall progress bar. Returns 0 when
    /// the server won't say, which the caller treats as "weight it equally".
    /// </summary>
    private async Task<long> TryGetContentLengthAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return 0;
            return response.Content.Headers.ContentLength ?? 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not size {Url}; weighting it equally in the progress bar.", url);
            return 0;
        }
    }

    /// <summary>Streams one file to <paramref name="partPath"/>, reporting percent read.</summary>
    private async Task DownloadFileAsync(string url, string partPath, IProgress<double>? progress, CancellationToken ct)
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

    /// <summary>Deletes an installed model folder. No-op if not present.</summary>
    public void Delete(string id)
    {
        try
        {
            var dir = PathFor(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Log.Information("Deleted Nemotron model {Id}.", id);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not delete Nemotron model {Id}.", id);
        }
    }

    /// <summary>
    /// Drops the half-written <c>.part</c> files after a failure, leaving any file that did
    /// complete so a retry resumes at file granularity instead of restarting the whole 650 MB.
    /// </summary>
    private void CleanupParts(string id)
    {
        foreach (var file in NemotronModelCatalog.Files)
        {
            try
            {
                var part = FilePathFor(id, file) + ".part";
                if (File.Exists(part)) File.Delete(part);
            }
            catch { /* ignore */ }
        }
    }

    public void Dispose() => _http.Dispose();
}
