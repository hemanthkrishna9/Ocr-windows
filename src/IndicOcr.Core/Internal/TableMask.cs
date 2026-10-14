using System.Drawing;
using SkiaSharp;

namespace IndicOcr.Core.Internal;

/// <summary>
/// Binary "ink" image used by the table detectors, reduced by an integer block factor so the longest side is at most about 2000 px.
/// A mask pixel is ink when at least a quarter of its block is darker than the Otsu threshold, so a 1 px ruling line survives the reduction.
/// </summary>
internal sealed class InkMask
{
    public int W { get; }
    public int H { get; }
    /// <summary>Image pixels per mask pixel.</summary>
    public int Factor { get; }
    public bool[] Data { get; }

    InkMask(int w, int h, int f, bool[] d) { W = w; H = h; Factor = f; Data = d; }

    public bool this[int x, int y] => Data[y * W + x];

    public static InkMask From(SKBitmap bmp, int maxSide = 2000)
    {
        SKBitmap? converted = null;
        try
        {
            var src = bmp;
            if (bmp.ColorType != SKColorType.Rgba8888 && bmp.ColorType != SKColorType.Bgra8888)
            {
                converted = bmp.Copy(SKColorType.Rgba8888) ?? throw new InvalidOperationException("Cannot read the bitmap pixels.");
                src = converted;
            }
            int w = src.Width, h = src.Height, stride = src.RowBytes;
            var px = src.GetPixelSpan();
            int f = Math.Max(1, (Math.Max(w, h) + maxSide - 1) / maxSide);

            // Otsu threshold over the gray levels (R and B weigh the same, so RGBA and BGRA agree).
            var hist = new long[256];
            for (int y = 0; y < h; y++)
            {
                int o = y * stride;
                for (int x = 0; x < w; x++, o += 4) hist[(px[o] + 2 * px[o + 1] + px[o + 2]) >> 2]++;
            }
            int t = Otsu(hist, (long)w * h);

            int mw = (w + f - 1) / f, mh = (h + f - 1) / f;
            var data = new bool[mw * mh];
            var counts = new int[mw];
            for (int my = 0; my < mh; my++)
            {
                Array.Clear(counts);
                int y1 = Math.Min(h, (my + 1) * f), area = 0;
                for (int y = my * f; y < y1; y++)
                {
                    int o = y * stride;
                    for (int x = 0; x < w; x++, o += 4)
                        if (((px[o] + 2 * px[o + 1] + px[o + 2]) >> 2) < t) counts[x / f]++;
                    area++;
                }
                for (int mx = 0; mx < mw; mx++)
                {
                    int bw = Math.Min(f, w - mx * f);
                    data[my * mw + mx] = counts[mx] * 4 >= bw * area;
                }
            }
            return new InkMask(mw, mh, f, data);
        }
        finally { converted?.Dispose(); }
    }

    static int Otsu(long[] hist, long total)
    {
        double sum = 0;
        for (int i = 0; i < 256; i++) sum += i * (double)hist[i];
        double sumB = 0, best = -1; long wB = 0; int thr = 128;
        for (int i = 0; i < 256; i++)
        {
            wB += hist[i];
            if (wB == 0) continue;
            long wF = total - wB;
            if (wF == 0) break;
            sumB += i * (double)hist[i];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; thr = i + 1; }
        }
        return Math.Clamp(thr, 80, 190); // pages with no clear dark/light split must not binarise to all-ink or all-white
    }

    /// <summary>True when any mask pixel of the image-space range [x0,x1) x [y0,y1) is ink; per-column ink flags are written to <paramref name="cols"/> (one per mask column from x0/Factor).</summary>
    public void ColumnInk(float x0, float x1, float y0, float y1, out int firstCol, out bool[] cols)
    {
        int a = Math.Clamp((int)(x0 / Factor), 0, W - 1), b = Math.Clamp((int)Math.Ceiling(x1 / Factor), a + 1, W);
        int ya = Math.Clamp((int)(y0 / Factor), 0, H - 1), yb = Math.Clamp((int)Math.Ceiling(y1 / Factor), ya + 1, H);
        cols = new bool[b - a];
        for (int y = ya; y < yb; y++)
        {
            int o = y * W;
            for (int x = a; x < b; x++) if (Data[o + x]) cols[x - a] = true;
        }
        firstCol = a;
    }
}
