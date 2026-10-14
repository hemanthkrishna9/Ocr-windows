using BitMiracle.LibTiff.Classic;
using PDFtoImage;
using SkiaSharp;

namespace IndicOcr.Core.Internal;

/// <summary>One decoded page and the resolution it is known to have (null when the file does not say).</summary>
internal readonly record struct PageSource(SKBitmap Bitmap, int? Dpi);

/// <summary>Reads png/jpg/bmp/tif/pdf into page bitmaps, one at a time, with caps on memory use.</summary>
internal static class DocumentReader
{
    public const int PdfDpi = 300;
    /// <summary>A PDF page is rendered at a lower dpi when its long side would exceed this many pixels (A0 at 300 dpi is 14000 px).</summary>
    public const int MaxPdfSidePixels = 8000;
    /// <summary>Images with more pixels than this are scaled down while decoding (when the codec can) or refused.</summary>
    public const long MaxImagePixels = 100_000_000;

    public static IEnumerable<PageSource> ReadPages(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf") return ReadPdf(path);
        if (ext is ".tif" or ".tiff") return ReadTiff(path);
        return ReadImage(path);
    }

    static IEnumerable<PageSource> ReadImage(string path)
    {
        var bmp = DecodeWithOrientation(path, out double scale);
        var dpi = ImageDpi.TryRead(path);
        yield return new PageSource(bmp, dpi == null ? null : Math.Max(20, (int)Math.Round(dpi.Value * scale)));
    }

    /// <summary>Decodes a file (no whole-file byte array), applies the EXIF orientation, and keeps the pixel count under <see cref="MaxImagePixels"/>.</summary>
    internal static SKBitmap DecodeWithOrientation(string path) => DecodeWithOrientation(path, out _);

    /// <param name="scale">Linear reduction applied while decoding (1 = full size).</param>
    internal static SKBitmap DecodeWithOrientation(string path, out double scale)
    {
        scale = 1;
        using var codec = SKCodec.Create(path)
            ?? throw new InvalidDataException($"Cannot decode image: {Path.GetFileName(path)}");
        var origin = codec.EncodedOrigin;
        var info = codec.Info;
        long pixels = (long)info.Width * info.Height;
        SKBitmap? bmp;
        if (pixels > MaxImagePixels)
        {
            // Ask the codec for the largest supported reduction (JPEG: 1/2, 1/4, 1/8) that fits the cap.
            SKSizeI size = new(info.Width, info.Height);
            for (float f = 0.5f; f >= 0.125f && (long)size.Width * size.Height > MaxImagePixels; f /= 2)
                size = codec.GetScaledDimensions(f);
            if ((long)size.Width * size.Height > MaxImagePixels)
                throw new InvalidDataException(
                    $"Image too large: {info.Width}x{info.Height} ({pixels / 1_000_000} MP); the limit is {MaxImagePixels / 1_000_000} MP. Downscale it first.");
            scale = (double)size.Width / info.Width;
            bmp = SKBitmap.Decode(codec, new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        }
        else bmp = SKBitmap.Decode(codec);
        if (bmp == null) throw new InvalidDataException($"Cannot decode image: {Path.GetFileName(path)}");
        return ApplyOrigin(bmp, origin);
    }

    /// <summary>Decodes in-memory image bytes (same rules as the file overload); kept for callers that already hold the bytes.</summary>
    internal static SKBitmap DecodeWithOrientation(byte[] data, string name)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data))
            ?? throw new InvalidDataException($"Cannot decode image: {Path.GetFileName(name)}");
        var origin = codec.EncodedOrigin;
        if ((long)codec.Info.Width * codec.Info.Height > MaxImagePixels)
            throw new InvalidDataException($"Image too large: {codec.Info.Width}x{codec.Info.Height}; the limit is {MaxImagePixels / 1_000_000} MP.");
        var bmp = SKBitmap.Decode(codec) ?? throw new InvalidDataException($"Cannot decode image: {Path.GetFileName(name)}");
        return ApplyOrigin(bmp, origin);
    }

    static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default) return src;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? src.Height : src.Width, h = swap ? src.Width : src.Height;
        var dst = new SKBitmap(new SKImageInfo(w, h, src.ColorType, src.AlphaType));
        using (var c = new SKCanvas(dst))
        {
            c.Clear(SKColors.White);
            var m = origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
                SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
                _ => SKMatrix.Identity,
            };
            c.SetMatrix(m);
            using var im = SKImage.FromBitmap(src);
            c.DrawImage(im, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        }
        src.Dispose();
        return dst;
    }

    /// <summary>The dpi to render a PDF page of the given size (points) at: 300, lowered so the long side stays within <see cref="MaxPdfSidePixels"/>.</summary>
    internal static int PdfRenderDpi(float widthPt, float heightPt)
    {
        float longPt = Math.Max(widthPt, heightPt);
        if (longPt <= 0) return PdfDpi;
        double dpi = Math.Min(PdfDpi, MaxPdfSidePixels * 72.0 / longPt);
        return Math.Max(20, (int)Math.Floor(dpi));
    }

    static IEnumerable<PageSource> ReadPdf(string path)
    {
        // byte[] overloads: the string overloads of PDFtoImage take base64 text, not a path.
        var pdf = File.ReadAllBytes(path);
        int count = Conversion.GetPageCount(pdf);
        for (int i = 0; i < count; i++)
        {
            var size = Conversion.GetPageSize(pdf, i);
            int dpi = PdfRenderDpi(size.Width, size.Height);
            var opt = new RenderOptions(Dpi: dpi, BackgroundColor: SKColors.White);
            yield return new PageSource(Conversion.ToImage(pdf, i, options: opt), dpi);
        }
    }

    static IEnumerable<PageSource> ReadTiff(string path)
    {
        using var tiff = Tiff.Open(path, "r") ?? throw new InvalidDataException($"Cannot open TIFF: {Path.GetFileName(path)}");
        int dirs = tiff.NumberOfDirectories();
        for (short d = 0; d < dirs; d++)
        {
            tiff.SetDirectory(d);
            int w = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            int h = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            if (w <= 0 || h <= 0 || (long)w * h > MaxImagePixels)
                throw new InvalidDataException($"TIFF page {d + 1} too large: {w}x{h}; the limit is {MaxImagePixels / 1_000_000} MP.");
            var raster = new int[w * h];
            if (!tiff.ReadRGBAImageOriented(w, h, raster, Orientation.TOPLEFT))
                throw new InvalidDataException($"Cannot decode TIFF page {d + 1}");
            var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            // ABGR-packed ints are R,G,B,A bytes on little-endian machines: copy straight into the bitmap.
            System.Runtime.InteropServices.Marshal.Copy(raster, 0, bmp.GetPixels(), raster.Length);
            raster = null!;
            yield return new PageSource(bmp, TiffDpi(tiff));
        }
    }

    static int? TiffDpi(Tiff tiff)
    {
        try
        {
            var xr = tiff.GetField(TiffTag.XRESOLUTION);
            if (xr == null || xr.Length == 0) return null;
            double res = xr[0].ToDouble();
            var unitField = tiff.GetField(TiffTag.RESOLUTIONUNIT);
            int unit = unitField is { Length: > 0 } ? unitField[0].ToInt() : 2;
            if (unit == 3) res *= 2.54;       // pixels per centimetre
            else if (unit != 2) return null;  // no absolute unit
            return ImageDpi.Sane(res);
        }
        catch { return null; }
    }
}
