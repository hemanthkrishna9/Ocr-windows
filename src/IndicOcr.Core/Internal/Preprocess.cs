using SkiaSharp;

namespace IndicOcr.Core.Internal;

/// <summary>Opaque BGRA pixels of a page (transparent areas composited on white).</summary>
internal sealed class PixelImage : IDisposable
{
    readonly SKBitmap _bmp;
    readonly bool _owns;
    public int Width => _bmp.Width;
    public int Height => _bmp.Height;
    public ReadOnlySpan<byte> Pixels => _bmp.GetPixelSpan();
    public SKBitmap Bitmap => _bmp;

    PixelImage(SKBitmap b, bool owns) { _bmp = b; _owns = owns; }

    /// <summary>
    /// Wraps an opaque BGRA bitmap as is (no copy: saves a full page of memory for a typical decoded scan; the caller keeps ownership
    /// and must not dispose it while this image is in use); any other format is converted into a new opaque BGRA copy.
    /// </summary>
    public static PixelImage From(SKBitmap src)
    {
        if (src.ColorType == SKColorType.Bgra8888 && src.AlphaType == SKAlphaType.Opaque && src.RowBytes == src.Width * 4)
            return new PixelImage(src, owns: false);
        var info = new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var dst = new SKBitmap(info);
        using (var canvas = new SKCanvas(dst))
        {
            canvas.Clear(SKColors.White);
            using var im = SKImage.FromBitmap(src);
            canvas.DrawImage(im, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        }
        return new PixelImage(dst, owns: true);
    }

    public void Dispose() { if (_owns) _bmp.Dispose(); }
}

internal static class Preprocess
{
    static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    /// <summary>PaddleOCR DetResizeForTest (limit_type=max): cap the long side, round both sides to a multiple of 32.</summary>
    public static (int W, int H) DetSize(int srcW, int srcH, int maxSide)
    {
        double ratio = 1.0;
        int longSide = Math.Max(srcW, srcH);
        if (longSide > maxSide) ratio = (double)maxSide / longSide;
        int w = RoundTo32(srcW * ratio), h = RoundTo32(srcH * ratio);
        return (w, h);
    }

    static int RoundTo32(double v) => Math.Max(32, (int)Math.Round(v / 32.0) * 32);

    /// <summary>NCHW float tensor in BGR order, (x/255 - mean)/std with PaddleOCR's mean/std applied to channels as stored (B,G,R).</summary>
    public static float[] DetTensor(PixelImage img, int w, int h)
    {
        ReadOnlySpan<byte> px;
        SKBitmap? resized = null;
        if (w == img.Width && h == img.Height) px = img.Pixels;
        else
        {
            resized = img.Bitmap.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            px = resized.GetPixelSpan();
        }
        try
        {
            var t = new float[3 * w * h];
            int plane = w * h;
            float s0 = 1f / (255f * Std[0]), s1 = 1f / (255f * Std[1]), s2 = 1f / (255f * Std[2]);
            float o0 = Mean[0] / Std[0], o1 = Mean[1] / Std[1], o2 = Mean[2] / Std[2];
            for (int i = 0, j = 0; i < plane; i++, j += 4)
            {
                t[i] = px[j] * s0 - o0;
                t[plane + i] = px[j + 1] * s1 - o1;
                t[2 * plane + i] = px[j + 2] * s2 - o2;
            }
            return t;
        }
        finally { resized?.Dispose(); }
    }

    public const int RecHeight = 48;
    public const int RecMinWidth = 320;

    /// <summary>Width of the resized line crop for a given aspect ratio (PaddleOCR resize_norm_img).</summary>
    public static int RecWidth(double aspect) => Math.Max(1, (int)Math.Ceiling(RecHeight * aspect));

    /// <summary>A line crop already resized to height 48, BGR bytes, HWC.</summary>
    internal sealed record LineCrop(byte[] Bgr, int Width, double Aspect);

    /// <summary>
    /// Cuts the quad (tl,tr,br,bl) out of the page with an affine (parallelogram) warp and resizes it to height 48,
    /// keeping aspect. Tall boxes (h/w &gt;= 1.5) are rotated 90 degrees counter-clockwise as PaddleOCR does, unless <paramref name="rotateTall"/> is false
    /// (upright single digits in narrow table cells are tall boxes that must be read as they stand).
    /// </summary>
    public static LineCrop Crop(ReadOnlySpan<byte> px, int imgW, int imgH, Pt[] q, bool rotateTall = true)
    {
        double w = Math.Max((q[1] - q[0]).Length, (q[2] - q[3]).Length);
        double h = Math.Max((q[3] - q[0]).Length, (q[2] - q[1]).Length);
        w = Math.Max(w, 1);
        h = Math.Max(h, 1);
        bool rotate = rotateTall && h / w >= 1.5;
        double outAspect = rotate ? h / w : w / h;
        // never create absurdly wide crops
        int outW = Math.Min(RecWidth(outAspect), 8192);
        int outH = RecHeight;

        // source step (source pixels per output pixel) to choose supersampling
        double srcLenX = rotate ? h : w, srcLenY = rotate ? w : h;
        double stepX = srcLenX / outW, stepY = srcLenY / outH;
        int tapsX = Math.Clamp((int)Math.Round(stepX), 1, 4), tapsY = Math.Clamp((int)Math.Round(stepY), 1, 4);

        var ex = q[1] - q[0];
        var ey = q[3] - q[0];
        var res = new byte[outW * outH * 3];
        double inv = 1.0 / (tapsX * tapsY);
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                double b = 0, g = 0, r = 0;
                for (int ty = 0; ty < tapsY; ty++)
                {
                    double t = (y + (ty + 0.5) / tapsY) / outH;
                    for (int tx = 0; tx < tapsX; tx++)
                    {
                        double s = (x + (tx + 0.5) / tapsX) / outW;
                        double a = rotate ? 1 - t : s;
                        double bb = rotate ? s : t;
                        double sxp = q[0].X + ex.X * a + ey.X * bb - 0.5;
                        double syp = q[0].Y + ex.Y * a + ey.Y * bb - 0.5;
                        Bilinear(px, imgW, imgH, sxp, syp, ref b, ref g, ref r);
                    }
                }
                int o = (y * outW + x) * 3;
                res[o] = (byte)Math.Clamp(b * inv + 0.5, 0, 255);
                res[o + 1] = (byte)Math.Clamp(g * inv + 0.5, 0, 255);
                res[o + 2] = (byte)Math.Clamp(r * inv + 0.5, 0, 255);
            }
        }
        return new LineCrop(res, outW, outAspect);
    }

    static void Bilinear(ReadOnlySpan<byte> px, int w, int h, double x, double y, ref double b, ref double g, ref double r)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        double fx = x - x0, fy = y - y0;
        int i00 = (y0 * w + x0) * 4, i10 = (y0 * w + x1) * 4, i01 = (y1 * w + x0) * 4, i11 = (y1 * w + x1) * 4;
        double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        b += px[i00] * w00 + px[i10] * w10 + px[i01] * w01 + px[i11] * w11;
        g += px[i00 + 1] * w00 + px[i10 + 1] * w10 + px[i01 + 1] * w01 + px[i11 + 1] * w11;
        r += px[i00 + 2] * w00 + px[i10 + 2] * w10 + px[i01 + 2] * w01 + px[i11 + 2] * w11;
    }

    /// <summary>NCHW tensor [n,3,48,width], (x/255-0.5)/0.5, zero padded on the right.</summary>
    public static float[] RecBatchTensor(IReadOnlyList<LineCrop> crops, int width)
    {
        int n = crops.Count, plane = RecHeight * width;
        var t = new float[n * 3 * plane];
        for (int i = 0; i < n; i++)
        {
            var c = crops[i];
            int cw = Math.Min(c.Width, width);
            int baseIdx = i * 3 * plane;
            for (int y = 0; y < RecHeight; y++)
            {
                int row = y * width;
                for (int x = 0; x < cw; x++)
                {
                    int s = (y * c.Width + x) * 3;
                    t[baseIdx + row + x] = c.Bgr[s] / 127.5f - 1f;
                    t[baseIdx + plane + row + x] = c.Bgr[s + 1] / 127.5f - 1f;
                    t[baseIdx + 2 * plane + row + x] = c.Bgr[s + 2] / 127.5f - 1f;
                }
            }
        }
        return t;
    }
}
