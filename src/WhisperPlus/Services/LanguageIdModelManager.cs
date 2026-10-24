using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using WhisperPlus.Models;

namespace WhisperPlus.Services;

/// <summary>
/// Downloads and locates the Silero language-identification ONNX file. Same shape as
/// <see cref="WhisperModelManager"/> — stream to <c>.part</c>, rename atomically — so an
/// interrupted download can never leave a truncated file that ONNX Runtime would fail to parse at
/// the worst possible moment, mid-dictation.
/// </summary>
public sealed class LanguageIdModelManager : IDisposable
{
    /// <summary>Subfolder of the shared models directory holding language-ID models.</summary>
    private const string SubFolder = "lid";

    /// <summary>
    /// Below this the file cannot be the real model, so a stray or truncated one is reported as
    /// not installed rather than handed to the runtime. The model is ~17 MB.
    /// </summary>
    private const long MinPlausibleBytes = 8L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;

    /// <summary>Download progress, 0..100. Raised on a background thread.</summary>
    public event Action<double>? DownloadProgress;

    /// <summary>Raised when the download finishes, with whether it succeeded.</summary>
    public event Action<bool>? DownloadCompleted;

    public LanguageIdModelManager(ISettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>Folder holding language-ID model files.</summary>
    public string ModelsDirectory =>
        Path.Combine(_settings.Current.WhisperModelsDirectory, SubFolder);

    /// <summary>Full path the model occupies on disk, whether or not it exists.</summary>
    public string ModelPath => Path.Combine(ModelsDirectory, LanguageIdModelCatalog.FileName);

    /// <summary>True when the model file is present and large enough to be the real thing.</summary>
    public bool IsInstalled
    {
        get
        {
            try
            {
                var path = ModelPath;
                return File.Exists(path) && new FileInfo(path).Length >= MinPlausibleBytes;
            }
            catch { return false; }
        }
    }

    /// <summary>On-disk size in MB, or 0 when not installed.</summary>
    public long InstalledSizeMb
    {
        get
        {
            try
            {
                var path = ModelPath;
                return File.Exists(path) ? new FileInfo(path).Length / (1024 * 1024) : 0;
            }
            catch { return 0; }
        }
    }

    /// <summary>
    /// Downloads the model, reporting transfer percent. Returns the final path. A already-installed
    /// model short-circuits. Cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<string> DownloadAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var finalPath = ModelPath;
        var partPath = finalPath + ".part";

        if (IsInstalled)
        {
            Log.Information("Language-ID model already present at {Path}; skipping download.", finalPath);
            DownloadCompleted?.Invoke(true);
            return finalPath;
        }

        Log.Information("Downloading language-ID model from {Url}", LanguageIdModelCatalog.Url);
        try
        {
            using var response = await _http.GetAsync(
                LanguageIdModelCatalog.Url, HttpCompletionOption.ResponseHeadersRead, ct);
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

                    // Throttle to ~10 reports/sec so the UI thread is not flooded.
                    if (totalBytes > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                    {
                        lastReport = DateTime.UtcNow;
                        var pct = readBytes * 100.0 / totalBytes;
                        progress?.Report(pct);
                        DownloadProgress?.Invoke(pct);
                    }
                }
            }

            if (readBytes < MinPlausibleBytes)
                throw new InvalidDataException(
                    $"Language-ID model download was only {readBytes} bytes; expected at least {MinPlausibleBytes}.");

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(partPath, finalPath);

            progress?.Report(100);
            DownloadProgress?.Invoke(100);
            DownloadCompleted?.Invoke(true);

            Log.Information("Language-ID model downloaded ({Mb} MB).", readBytes / (1024 * 1024));
            return finalPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            DownloadCompleted?.Invoke(false);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Language-ID model download failed.");
            TryDelete(partPath);
            DownloadCompleted?.Invoke(false);
            throw;
        }
    }

    /// <summary>Deletes the model file. No-op when not present.</summary>
    public void Delete()
    {
        try
        {
            var path = ModelPath;
            if (File.Exists(path)) File.Delete(path);
            TryDelete(path + ".part");
            Log.Information("Deleted language-ID model.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not delete language-ID model.");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* ignore */ }
    }

    public void Dispose() => _http.Dispose();
}
