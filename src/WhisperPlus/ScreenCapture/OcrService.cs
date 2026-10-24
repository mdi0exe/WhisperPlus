using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WhisperPlus.ScreenCapture;

/// <summary>Outcome of one recognition pass.</summary>
/// <param name="Text">The recognized text, lines separated by <c>\n</c>; empty when nothing was found.</param>
/// <param name="Language">The BCP-47 tag of the engine that ran, for the status message.</param>
public sealed record OcrResult(string Text, string Language)
{
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Offline text recognition for the screenshot module, using the OCR engine that ships with Windows
/// (<c>Windows.Media.Ocr</c>).
///
/// <para>Nothing is downloaded and nothing leaves the machine: the recognizer comes with the
/// language pack the user already has installed, which is why this is a button on the overlay rather
/// than another few hundred megabytes of model. The trade-off is that a language can only be
/// recognized if its OCR pack is installed — <see cref="InstalledLanguages"/> reports what is
/// available, and the UI says so instead of silently returning nothing.</para>
/// </summary>
public static class OcrService
{
    private static readonly Lazy<OcrEngine?> Engine = new(CreateEngine, isThreadSafe: true);

    /// <summary>BCP-47 tags of the OCR languages available on this machine.</summary>
    public static IReadOnlyList<string> InstalledLanguages()
    {
        var tags = new List<string>();
        try
        {
            foreach (var language in OcrEngine.AvailableRecognizerLanguages)
                tags.Add(language.LanguageTag);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not enumerate the installed OCR languages.");
        }
        return tags;
    }

    /// <summary>True when at least one OCR language pack is installed.</summary>
    public static bool IsAvailable => Engine.Value is not null;

    private static OcrEngine? CreateEngine()
    {
        try
        {
            // The user's own language list first, so a Persian or English desktop recognizes without
            // being told which language the picture is in.
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is not null)
            {
                Log.Information("OCR engine ready ({Language}).", engine.RecognizerLanguage.LanguageTag);
                return engine;
            }

            // No profile language has an OCR pack: fall back to whatever is installed.
            foreach (var language in OcrEngine.AvailableRecognizerLanguages)
            {
                engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine is not null)
                {
                    Log.Information("OCR engine ready ({Language}, from the installed list).", language.LanguageTag);
                    return engine;
                }
            }

            Log.Information("No OCR language pack is installed; the OCR button will report that.");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not create the OCR engine.");
            return null;
        }
    }

    /// <summary>
    /// Recognizes the text in <paramref name="bitmap"/>. Returns null when no engine is available, so
    /// the caller can distinguish "not installed" from "nothing found".
    /// </summary>
    public static async Task<OcrResult?> RecognizeAsync(SKBitmap bitmap)
    {
        if (bitmap is null || bitmap.Width < 2 || bitmap.Height < 2) return null;

        var engine = Engine.Value;
        if (engine is null) return null;

        try
        {
            using var software = ToSoftwareBitmap(bitmap);
            var result = await engine.RecognizeAsync(software);

            // OcrResult.Text joins the words of a line with spaces but gives no say in how lines are
            // separated, and a screenshot is mostly lines: build the text line by line so the copied
            // text keeps the layout the user sees.
            var text = new StringBuilder();
            foreach (var line in result.Lines)
            {
                if (text.Length > 0) text.Append('\n');
                for (int i = 0; i < line.Words.Count; i++)
                {
                    if (i > 0) text.Append(' ');
                    text.Append(line.Words[i].Text);
                }
            }

            Log.Information("OCR: {Chars} characters in {Lines} lines ({Language}).",
                text.Length, result.Lines.Count, engine.RecognizerLanguage.LanguageTag);
            return new OcrResult(text.ToString(), engine.RecognizerLanguage.LanguageTag);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OCR failed.");
            return null;
        }
    }

    /// <summary>
    /// Recognizes text from a WPF <see cref="System.Windows.Media.Imaging.BitmapSource"/> directly.
    /// </summary>
    public static async Task<OcrResult?> RecognizeAsync(System.Windows.Media.Imaging.BitmapSource source)
    {
        if (source is null || source.PixelWidth < 2 || source.PixelHeight < 2) return null;
        var engine = Engine.Value;
        if (engine is null) return null;

        try
        {
            var formatted = source;
            if (source.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                formatted = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            }

            int width = formatted.PixelWidth;
            int height = formatted.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            formatted.CopyPixels(pixels, stride, 0);

            using var software = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            software.CopyFromBuffer(pixels.AsBuffer());

            var result = await engine.RecognizeAsync(software);
            var text = new StringBuilder();
            foreach (var line in result.Lines)
            {
                if (text.Length > 0) text.Append('\n');
                for (int i = 0; i < line.Words.Count; i++)
                {
                    if (i > 0) text.Append(' ');
                    text.Append(line.Words[i].Text);
                }
            }

            Log.Information("OCR (BitmapSource): {Chars} characters in {Lines} lines ({Language}).",
                text.Length, result.Lines.Count, engine.RecognizerLanguage.LanguageTag);
            return new OcrResult(text.ToString(), engine.RecognizerLanguage.LanguageTag);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OCR from BitmapSource failed.");
            return null;
        }
    }

    /// <summary>
    /// Copies the pixels into the WinRT bitmap the recognizer expects. The engine takes BGRA8, so a
    /// bitmap in another colour type is converted first rather than reinterpreted.
    /// </summary>
    private static SoftwareBitmap ToSoftwareBitmap(SKBitmap bitmap)
    {
        var source = bitmap.ColorType == SKColorType.Bgra8888
            ? null
            : bitmap.Copy(SKColorType.Bgra8888);

        try
        {
            var pixels = (source ?? bitmap).GetPixelSpan();
            var target = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Premultiplied);
            target.CopyFromBuffer(pixels.ToArray().AsBuffer());
            return target;
        }
        finally
        {
            source?.Dispose();
        }
    }
}
