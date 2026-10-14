using BitMiracle.LibTiff.Classic;
using PDFtoImage;
using SkiaSharp;

namespace IndicOcr.Core.Internal;

/// <summary>Reads png/jpg/bmp/tif/pdf into page bitmaps, one at a time.</summary>
internal static class DocumentReader
{
    public const int PdfDpi = 300;

    public static IEnumerable<SKBitmap> ReadPages(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf") return ReadPdf(path);
        if (ext is ".tif" or ".tiff") return ReadTiff(path);
        return ReadImage(path);
    }

    static IEnumerable<SKBitmap> ReadImage(string path)
    {
        yield return DecodeWithOrientation(File.ReadAllBytes(path), path);
    }

    internal static SKBitmap DecodeWithOrientation(byte[] data, string name)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data))
            ?? throw new InvalidDataException($"Cannot decode image: {Path.GetFileName(name)}");
        var origin = codec.EncodedOrigin;
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

    static IEnumerable<SKBitmap> ReadPdf(string path)
    {
        // byte[] overloads: the string overloads of PDFtoImage take base64 text, not a path.
        var pdf = File.ReadAllBytes(path);
        int count = Conversion.GetPageCount(pdf);
        var opt = new RenderOptions(Dpi: PdfDpi, BackgroundColor: SKColors.White);
        for (int i = 0; i < count; i++)
            yield return Conversion.ToImage(pdf, i, options: opt);
    }

    static IEnumerable<SKBitmap> ReadTiff(string path)
    {
        using var tiff = Tiff.Open(path, "r") ?? throw new InvalidDataException($"Cannot open TIFF: {Path.GetFileName(path)}");
        int dirs = tiff.NumberOfDirectories();
        for (short d = 0; d < dirs; d++)
        {
            tiff.SetDirectory(d);
            int w = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            int h = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            var raster = new int[w * h];
            if (!tiff.ReadRGBAImageOriented(w, h, raster, Orientation.TOPLEFT))
                throw new InvalidDataException($"Cannot decode TIFF page {d + 1}");
            var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var bytes = new byte[w * h * 4];
            for (int i = 0; i < raster.Length; i++)
            {
                int v = raster[i];
                bytes[i * 4] = (byte)(v & 0xFF);          // R
                bytes[i * 4 + 1] = (byte)((v >> 8) & 0xFF);
                bytes[i * 4 + 2] = (byte)((v >> 16) & 0xFF);
                bytes[i * 4 + 3] = (byte)((v >> 24) & 0xFF);
            }
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
            yield return bmp;
        }
    }
}
