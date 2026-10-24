using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Serilog;
using SkiaSharp;
using WhisperPlus.ScreenCapture;

namespace WhisperPlus.ScreenCapture.Editor;

/// <summary>Sahneyi kodlar (PNG/JPEG/WebP), panoya kopyalar ve dosyaya kaydeder.</summary>
public static class ImageExporter
{
    // Litterbox geçici yükleme sınırı: 1 GB.
    public const long UploadMaxBytes = 1_000_000_000;

    public static SKData Encode(SKBitmap bmp, ImageFormat format, int quality)
    {
        var (skFormat, q) = format switch
        {
            ImageFormat.Jpeg => (SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100)),
            ImageFormat.Webp => (SKEncodedImageFormat.Webp, Math.Clamp(quality, 1, 100)),
            _ => (SKEncodedImageFormat.Png, 100),
        };

        // JPEG saydamlığı desteklemez → beyaz zemine düzleştir.
        if (skFormat == SKEncodedImageFormat.Jpeg)
        {
            using var flat = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var c = new SKCanvas(flat))
            {
                c.Clear(SKColors.White);
                c.DrawBitmap(bmp, 0, 0);
            }
            using var img = SKImage.FromBitmap(flat);
            return img.Encode(skFormat, q);
        }
        else
        {
            using var img = SKImage.FromBitmap(bmp);
            return img.Encode(skFormat, q);
        }
    }

    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "jpg",
        ImageFormat.Webp => "webp",
        _ => "png",
    };

    public static string MimeType(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Webp => "image/webp",
        _ => "image/png",
    };

    public static (byte[] Bytes, string MimeType) EncodeForUpload(SKBitmap bmp, bool preserveTransparency = false)
    {
        using var pngData = Encode(bmp, ImageFormat.Png, 100);
        var pngBytes = pngData.ToArray();
        if (pngBytes.Length <= UploadMaxBytes)
            return (pngBytes, MimeType(ImageFormat.Png));

        if (preserveTransparency)
            throw new InvalidOperationException("The transparent PNG would exceed the 1 GB limit. Select a smaller area or use a background.");

        foreach (int quality in new[] { 92, 85, 78, 70, 62, 54 })
        {
            using var jpegData = Encode(bmp, ImageFormat.Jpeg, quality);
            var jpegBytes = jpegData.ToArray();
            if (jpegBytes.Length <= UploadMaxBytes)
                return (jpegBytes, MimeType(ImageFormat.Jpeg));
        }

        throw new InvalidOperationException("The image would exceed the 1 GB upload limit. Select a smaller area.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalFree(nint hMem);

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;

    /// <summary>Win32 clipboard ile CF_DIB + CF_DIBV5 (düz alfa) + PNG / image/png koyar.</summary>
    public static void CopyToClipboard(SKBitmap bmp)
    {
        // Pre-convert and pre-compress pixel buffers outside OpenClipboard
        using var pngData = Encode(bmp, ImageFormat.Png, 100);
        var pngBytes = pngData.ToArray();

        var dibV5Mem = CreateDibV5(bmp);
        var dibMem = CreateDib(bmp);
        var pngMem = CreateBytesBlock(pngBytes);
        var imagePngMem = CreateBytesBlock(pngBytes);

        uint cfPng = RegisterClipboardFormatW("PNG");
        uint cfImagePng = RegisterClipboardFormatW("image/png");

        bool opened = false;
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(nint.Zero))
            {
                opened = true;
                break;
            }
            Thread.Sleep(20);
        }

        if (!opened)
        {
            if (dibV5Mem != nint.Zero) GlobalFree(dibV5Mem);
            if (dibMem != nint.Zero) GlobalFree(dibMem);
            if (pngMem != nint.Zero) GlobalFree(pngMem);
            if (imagePngMem != nint.Zero) GlobalFree(imagePngMem);
            throw new InvalidOperationException("Could not open the clipboard after retries.");
        }

        try
        {
            EmptyClipboard();
            if (dibV5Mem != nint.Zero) { Publish(CF_DIBV5, dibV5Mem); dibV5Mem = nint.Zero; }
            if (dibMem != nint.Zero) { Publish(CF_DIB, dibMem); dibMem = nint.Zero; }
            if (cfPng != 0 && pngMem != nint.Zero) { Publish(cfPng, pngMem); pngMem = nint.Zero; }
            if (cfImagePng != 0 && imagePngMem != nint.Zero) { Publish(cfImagePng, imagePngMem); imagePngMem = nint.Zero; }
        }
        finally
        {
            if (dibV5Mem != nint.Zero) GlobalFree(dibV5Mem);
            if (dibMem != nint.Zero) GlobalFree(dibMem);
            if (pngMem != nint.Zero) GlobalFree(pngMem);
            if (imagePngMem != nint.Zero) GlobalFree(imagePngMem);

            CloseClipboard();
        }
    }

    /// <summary>
    /// Hands <paramref name="hMem"/> to the clipboard, freeing it if the clipboard refuses it.
    ///
    /// <para>On success the clipboard owns the block and must not be freed here. On failure nothing
    /// else will ever reference it, so leaving it allocated leaked the whole image (megabytes per
    /// failed copy) until the process exited.</para>
    /// </summary>
    private static void Publish(uint format, nint hMem)
    {
        if (SetClipboardData(format, hMem) == nint.Zero)
        {
            GlobalFree(hMem);
            Log.Warning("SetClipboardData(0x{Format:X}) failed; released the buffer (err {Err}).",
                format, Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Yaygın uyumluluk için sade BITMAPINFOHEADER'lı CF_DIB.</summary>
    private static unsafe nint CreateDib(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int stride = w * 4;
        int pixelSize = stride * h;
        const int headerSize = 40; // BITMAPINFOHEADER

        var hMem = GlobalAlloc(GMEM_MOVEABLE, (nuint)(headerSize + pixelSize));
        if (hMem == nint.Zero) return nint.Zero;
        var ptr = GlobalLock(hMem);
        if (ptr == nint.Zero)
        {
            GlobalFree(hMem);
            return nint.Zero;
        }

        try
        {
            var span = new Span<byte>((void*)ptr, headerSize + pixelSize);
            span.Clear();
            using var writer = new MemoryStream(headerSize);
            using var bw = new BinaryWriter(writer);

            bw.Write(headerSize);   // biSize
            bw.Write(w);            // biWidth
            bw.Write(h);            // biHeight (positive = bottom-up)
            bw.Write((short)1);     // biPlanes
            bw.Write((short)32);    // biBitCount
            bw.Write(0);            // biCompression = BI_RGB
            bw.Write(pixelSize);    // biSizeImage
            bw.Write(0);            // biXPelsPerMeter
            bw.Write(0);            // biYPelsPerMeter
            bw.Write(0);            // biClrUsed
            bw.Write(0);            // biClrImportant

            writer.ToArray().CopyTo(span);
            CopyUnpremultipliedBgraBottomUp(bmp, span.Slice(headerSize));
            return hMem;
        }
        catch
        {
            GlobalFree(hMem);
            return nint.Zero;
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    /// <summary>
    /// Panodan görüntü okur; PNG (alfa) tercih edilir. WPF GetImage DIB'i beyaza düzler.
    /// </summary>
    public static SKBitmap? TryGetClipboardBitmap()
    {
        if (!OpenClipboard(nint.Zero))
            return null;
        try
        {
            foreach (string name in new[] { "PNG", "image/png" })
            {
                uint fmt = RegisterClipboardFormatW(name);
                if (fmt == 0) continue;
                var hMem = GetClipboardData(fmt);
                if (hMem == nint.Zero) continue;
                nuint size = GlobalSize(hMem);
                if (size == 0 || size > 64 * 1024 * 1024) continue;
                var ptr = GlobalLock(hMem);
                if (ptr == nint.Zero) continue;
                try
                {
                    var bytes = new byte[(int)size];
                    Marshal.Copy(ptr, bytes, 0, bytes.Length);
                    var decoded = SKBitmap.Decode(bytes);
                    if (decoded != null)
                        return decoded;
                }
                finally
                {
                    GlobalUnlock(hMem);
                }
            }
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static unsafe nint CreateDibV5(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int stride = w * 4;
        int pixelSize = stride * h;
        int headerSize = 124; // BITMAPV5HEADER

        var hMem = GlobalAlloc(GMEM_MOVEABLE, (nuint)(headerSize + pixelSize));
        if (hMem == nint.Zero) return nint.Zero;
        var ptr = GlobalLock(hMem);
        if (ptr == nint.Zero)
        {
            GlobalFree(hMem);
            return nint.Zero;
        }

        try
        {
            var span = new Span<byte>((void*)ptr, headerSize + pixelSize);
            span.Clear();
            using var writer = new MemoryStream(headerSize);
            using var bw = new BinaryWriter(writer);

            bw.Write(headerSize);       // bV5Size
            bw.Write(w);                // bV5Width
            bw.Write(h);                // bV5Height (positive = bottom-up)
            bw.Write((short)1);         // bV5Planes
            bw.Write((short)32);        // bV5BitCount
            bw.Write(3);                // bV5Compression = BI_BITFIELDS
            bw.Write(pixelSize);        // bV5SizeImage
            bw.Write(0);                // bV5XPelsPerMeter
            bw.Write(0);                // bV5YPelsPerMeter
            bw.Write(0);                // bV5ClrUsed
            bw.Write(0);                // bV5ClrImportant
            bw.Write(0x00FF0000);       // bV5RedMask
            bw.Write(0x0000FF00);       // bV5GreenMask
            bw.Write(0x000000FF);       // bV5BlueMask
            bw.Write(unchecked((int)0xFF000000)); // bV5AlphaMask
            bw.Write(0x73524742);       // bV5CSType = "sRGB"
            bw.Write(new byte[36]);     // bV5Endpoints (CIEXYZTRIPLE)
            bw.Write(0);                // bV5GammaRed
            bw.Write(0);                // bV5GammaGreen
            bw.Write(0);               // bV5GammaBlue
            bw.Write(0);                // bV5Intent = LCS_GM_IMAGES
            bw.Write(0);                // bV5ProfileData
            bw.Write(0);               // bV5ProfileSize
            bw.Write(0);                // bV5Reserved

            writer.ToArray().CopyTo(span);

            // Pixel data — bottom-up + düz (unpremultiplied) alfa.
            // Premul DIBV5 birçok uygulamada şeffaf bölgeyi beyaz/siyah gösterir.
            CopyUnpremultipliedBgraBottomUp(bmp, span.Slice(headerSize));
            return hMem;
        }
        catch
        {
            GlobalFree(hMem);
            return nint.Zero;
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    /// <summary>Premul BGRA → düz BGRA, satırlar alttan üste (DIB).</summary>
    internal static void CopyUnpremultipliedBgraBottomUp(SKBitmap bmp, Span<byte> dest)
    {
        int w = bmp.Width, h = bmp.Height;
        int stride = w * 4;
        var src = bmp.GetPixelSpan();
        var srcBytes = MemoryMarshal.AsBytes(src);
        for (int y = 0; y < h; y++)
        {
            var srcRow = srcBytes.Slice((h - 1 - y) * stride, stride);
            var dstRow = dest.Slice(y * stride, stride);
            UnpremultiplyBgraRow(srcRow, dstRow);
        }
    }

    internal static void UnpremultiplyBgraRow(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        for (int i = 0; i < src.Length; i += 4)
        {
            byte b = src[i], g = src[i + 1], r = src[i + 2], a = src[i + 3];
            if (a is 0 or 255)
            {
                dst[i] = b;
                dst[i + 1] = g;
                dst[i + 2] = r;
                dst[i + 3] = a;
                continue;
            }

            dst[i] = (byte)Math.Min(255, (b * 255 + a / 2) / a);
            dst[i + 1] = (byte)Math.Min(255, (g * 255 + a / 2) / a);
            dst[i + 2] = (byte)Math.Min(255, (r * 255 + a / 2) / a);
            dst[i + 3] = a;
        }
    }

    private static nint CreateBytesBlock(byte[] bytes)
    {
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (hMem == nint.Zero) return nint.Zero;
        var ptr = GlobalLock(hMem);
        if (ptr == nint.Zero)
        {
            GlobalFree(hMem);
            return nint.Zero;
        }

        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return hMem;
        }
        catch
        {
            GlobalFree(hMem);
            return nint.Zero;
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    /// <summary>Dosyaya kaydeder, tam yolu döner.</summary>
    public static string SaveToFile(SKBitmap bmp, string directory, ImageFormat format, int quality)
    {
        Directory.CreateDirectory(directory);
        string name = $"WhisperPlus_{DateTime.Now:yyyyMMdd_HHmmss}.{Extension(format)}";
        string path = Path.Combine(directory, name);
        using var data = Encode(bmp, format, quality);
        SaveEncoded(data, path);
        return path;
    }

    internal static void SaveEncoded(SKData data, string path)
    {
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    /// <summary>Sahneyi kodlanmış byte dizisine çevirir (yükleme için).</summary>
    public static byte[] EncodeScene(Scene scene, ImageFormat format, int quality)
    {
        using var bmp = SceneRenderer.RenderToBitmap(scene);
        using var data = Encode(bmp, format, quality);
        return data.ToArray();
    }
}
