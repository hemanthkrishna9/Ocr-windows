using System.Drawing;
using System.Globalization;
using System.Text;

namespace IndicOcr.Core.Internal;

/// <summary>Maps a character span of an OCR line to an approximate (and generously widened) quadrilateral in page pixels, and builds display masks.</summary>
internal static class PiiGeometry
{
    const double PadAlongLineHeights = 0.6, PadAlongLineFraction = 0.04, PadAcrossHeights = 0.25;

    static bool IsVirama(char c) => c is '्' or '்' or '్' or '್';

    /// <summary>Rough advance width of each char in em: digits 0.57, upper 0.65, lower 0.55, space 0.26, Indic letters 0.9 (0.1 after a virama, because the conjunct is stacked), marks 0 or 0.35.</summary>
    internal static double[] Weights(string text)
    {
        var w = new double[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            var cat = char.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark || IsVirama(c) || c is '‌' or '‍') w[i] = 0;
            else if (cat == UnicodeCategory.SpacingCombiningMark) w[i] = 0.35;
            else if (c == ' ' || c == ' ') w[i] = 0.26;
            else if (c >= '0' && c <= '9') w[i] = 0.57;
            else if (c >= 'A' && c <= 'Z') w[i] = 0.65;
            else if (c >= 'a' && c <= 'z') w[i] = 0.55;
            else if (c < 0x80) w[i] = 0.3;
            else if (c >= 0x0900 && char.IsLetterOrDigit(c)) w[i] = i > 0 && IsVirama(text[i - 1]) ? 0.1 : 0.9;
            else w[i] = 0.8;
        }
        return w;
    }

    public static PointF[] RegionQuad(OcrLine line, int start, int length, int pageW, int pageH)
    {
        var (tl, tr, br, bl) = Corners(line);
        double ux = tr.X - tl.X, uy = tr.Y - tl.Y, vx = bl.X - tl.X, vy = bl.Y - tl.Y;
        double lenU = Math.Sqrt(ux * ux + uy * uy), lenV = Math.Sqrt(vx * vx + vy * vy);
        if (lenU < 1e-3) lenU = 1e-3;

        var w = Weights(line.Text);
        double total = w.Sum(), before = 0, inside = 0;
        for (int i = 0; i < w.Length; i++)
        {
            if (i < start) before += w[i];
            else if (i < start + length) inside += w[i];
        }
        double a0, a1;
        if (total <= 0) { a0 = 0; a1 = 1; }
        else { a0 = before / total; a1 = (before + inside) / total; }

        double padA = (PadAlongLineHeights * lenV) / lenU + PadAlongLineFraction;
        double padB = lenV > 0 ? PadAcrossHeights : 0;
        a0 -= padA; a1 += padA;
        double b0 = -padB, b1 = 1 + padB;

        PointF P(double a, double b) => new((float)(tl.X + a * ux + b * vx), (float)(tl.Y + a * uy + b * vy));
        return new[] { P(a0, b0), P(a1, b0), P(a1, b1), P(a0, b1) };
    }

    /// <summary>Top-left, top-right, bottom-right, bottom-left of the line from its polygon (any order, near-horizontal text), else from its box.</summary>
    static (PointF tl, PointF tr, PointF br, PointF bl) Corners(OcrLine line)
    {
        var pg = line.Polygon;
        if (pg is { Length: 4 } && pg.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)))
        {
            var tl = pg.MinBy(p => p.X + p.Y);
            var br = pg.MaxBy(p => p.X + p.Y);
            var tr = pg.MaxBy(p => p.X - p.Y);
            var bl = pg.MinBy(p => p.X - p.Y);
            if (tl != tr && tr != br && br != bl && bl != tl && tl != br && tr != bl) return (tl, tr, br, bl);
        }
        var b = line.Box;
        return (new PointF(b.Left, b.Top), new PointF(b.Right, b.Top), new PointF(b.Right, b.Bottom), new PointF(b.Left, b.Bottom));
    }

    public static RectangleF Bounds(PointF[] quad, int pageW, int pageH)
    {
        float l = Math.Max(0, quad.Min(p => p.X)), t = Math.Max(0, quad.Min(p => p.Y));
        float r = Math.Min(pageW, quad.Max(p => p.X)), b = Math.Min(pageH, quad.Max(p => p.Y));
        return new RectangleF(l, t, Math.Max(0, r - l), Math.Max(0, b - t));
    }

    /// <summary>Hides all but the last four letters/digits ("XXXX XXXX 1234"); an e-mail keeps its first letter and domain.</summary>
    public static string MaskForDisplay(PiiKind kind, string matched)
    {
        if (kind == PiiKind.Email)
        {
            int at = matched.IndexOf('@');
            return at <= 0 ? "X***" : matched[0] + "***" + matched[at..];
        }
        int alnum = matched.Count(char.IsLetterOrDigit);
        int keepFrom = Math.Max(0, alnum - 4), seen = 0;
        var sb = new StringBuilder(matched.Length);
        foreach (char c in matched)
        {
            if (!char.IsLetterOrDigit(c)) { sb.Append(c); continue; }
            sb.Append(seen++ >= keepFrom ? (c > 0x7F && char.IsDigit(c) ? (char)('0' + (int)char.GetNumericValue(c)) : c) : 'X');
        }
        return sb.ToString();
    }
}
