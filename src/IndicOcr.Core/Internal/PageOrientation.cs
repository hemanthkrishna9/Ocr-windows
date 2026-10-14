using SkiaSharp;

namespace IndicOcr.Core.Internal;

/// <summary>Quarter-turn helpers for the page orientation step. Turns are clockwise, in units of 90 degrees (0..3).</summary>
internal static class PageOrientation
{
    /// <summary>Width of the long side / short side of a detected quad (as PaddleOCR's crop sees them).</summary>
    public static (double W, double H) Sides(Pt[] q)
    {
        double w = Math.Max((q[1] - q[0]).Length, (q[2] - q[3]).Length);
        double h = Math.Max((q[3] - q[0]).Length, (q[2] - q[1]).Length);
        return (Math.Max(w, 1), Math.Max(h, 1));
    }

    /// <summary>Fraction of boxes that are tall (height more than 1.5 x width).</summary>
    public static double TallFraction(IReadOnlyList<Pt[]> quads)
    {
        if (quads.Count == 0) return 0;
        int tall = 0;
        foreach (var q in quads) { var (w, h) = Sides(q); if (h > 1.5 * w) tall++; }
        return (double)tall / quads.Count;
    }

    /// <summary>
    /// The same quad re-ordered so that corner 0 is the top-left of the page after turning it by <paramref name="turns"/> clockwise
    /// quarter turns. Cropping the ORIGINAL pixels with this quad gives the crop the turned page would give.
    /// </summary>
    public static Pt[] Reorder(Pt[] q, int turns)
    {
        turns = ((turns % 4) + 4) % 4;
        var r = new Pt[4];
        for (int i = 0; i < 4; i++) r[i] = q[(i - turns + 8) % 4];
        return r;
    }

    /// <summary>Maps a quad of the original page into the frame of the page turned clockwise by <paramref name="turns"/> quarter turns (reading-order corners).</summary>
    public static Pt[] MapQuad(Pt[] q, int turns, int srcW, int srcH)
    {
        turns = ((turns % 4) + 4) % 4;
        var m = new Pt[4];
        for (int i = 0; i < 4; i++)
        {
            var p = q[i];
            m[i] = turns switch
            {
                1 => new Pt(srcH - p.Y, p.X),
                2 => new Pt(srcW - p.X, srcH - p.Y),
                3 => new Pt(p.Y, srcW - p.X),
                _ => p,
            };
        }
        return Reorder(m, turns);
    }

    /// <summary>A new bitmap holding the page turned clockwise by <paramref name="turns"/> quarter turns.</summary>
    public static SKBitmap Rotate(SKBitmap src, int turns)
    {
        turns = ((turns % 4) + 4) % 4;
        if (turns == 0) return src.Copy();
        bool swap = turns != 2;
        int w = swap ? src.Height : src.Width, h = swap ? src.Width : src.Height;
        var dst = new SKBitmap(new SKImageInfo(w, h, src.ColorType, src.AlphaType));
        using var c = new SKCanvas(dst);
        c.Clear(SKColors.White);
        var m = turns switch
        {
            1 => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),   // (x,y) -> (H - y, x)
            2 => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            _ => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),   // (x,y) -> (y, W - x)
        };
        c.SetMatrix(m);
        using var im = SKImage.FromBitmap(src);
        c.DrawImage(im, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        return dst;
    }
}
