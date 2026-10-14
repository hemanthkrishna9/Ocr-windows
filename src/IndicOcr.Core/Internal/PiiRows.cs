using System.Drawing;
using System.Text;

namespace IndicOcr.Core.Internal;

/// <summary>Several OCR lines read as one: their texts joined by single spaces. <see cref="Offsets"/> holds where each source line starts in <see cref="Text"/>.</summary>
internal sealed record PiiVirtualLine(IReadOnlyList<int> Lines, string Text, IReadOnlyList<int> Offsets, bool Continuation);

/// <summary>
/// Builds the "virtual lines" the detector runs on. The OCR detector splits a line wherever the gap is wide, so a number printed
/// large with wide group gaps ("2341   2341   2346") arrives as three lines. Lines on one baseline (vertical overlap of at least half
/// the smaller height) and no more than three line heights apart are joined left to right. A row that ends with a keyword
/// ("Aadhaar No:") is also joined with the row directly below it, as an extra virtual line.
/// </summary>
internal static class PiiRows
{
    public static List<PiiVirtualLine> Build(OcrPage page)
    {
        var lines = page.Lines;
        var order = Enumerable.Range(0, lines.Count).Where(i => !string.IsNullOrEmpty(lines[i].Text)).OrderBy(i => lines[i].Box.Left).ThenBy(i => i).ToList();
        var rows = new List<List<int>>();
        foreach (int i in order)
        {
            int best = -1; float bestGap = float.MaxValue;
            for (int r = 0; r < rows.Count; r++)
            {
                var a = lines[rows[r][^1]].Box; var b = lines[i].Box;
                if (!SameRow(a, b)) continue;
                float gap = b.Left - a.Right;
                if (gap < bestGap) { bestGap = gap; best = r; }
            }
            if (best >= 0) rows[best].Add(i); else rows.Add(new List<int> { i });
        }
        rows.Sort((x, y) => x.Min().CompareTo(y.Min()));

        var result = rows.Select(r => Make(page, r, false)).ToList();

        // Keyword at the end of a row, number on the row below.
        var bounds = rows.Select(r => Union(page, r)).ToList();
        for (int r = 0; r < rows.Count; r++)
        {
            if (!PiiDetector.EndsWithLabel(result[r].Text)) continue;
            var a = bounds[r];
            float h = Math.Max(1, a.Height);
            int below = -1; float bestTop = float.MaxValue;
            for (int q = 0; q < rows.Count; q++)
            {
                if (q == r) continue;
                var b = bounds[q];
                if (b.Top < a.Bottom - 0.5f * h || b.Top - a.Bottom > 2.5f * h) continue;
                bool xNear = b.Left < a.Right && a.Left < b.Right || Math.Abs(b.Left - a.Left) <= 3 * h;
                if (xNear && b.Top < bestTop) { bestTop = b.Top; below = q; }
            }
            if (below >= 0) result.Add(Make(page, rows[r].Concat(rows[below]).ToList(), true));
        }
        return result;
    }

    static PiiVirtualLine Make(OcrPage page, List<int> idx, bool continuation)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        foreach (int i in idx)
        {
            if (sb.Length > 0) sb.Append(' ');
            offsets.Add(sb.Length);
            sb.Append(page.Lines[i].Text);
        }
        return new PiiVirtualLine(idx, sb.ToString(), offsets, continuation);
    }

    static bool Finite(RectangleF b) => float.IsFinite(b.Left) && float.IsFinite(b.Top) && float.IsFinite(b.Width) && float.IsFinite(b.Height) && b.Height > 0;

    static bool SameRow(RectangleF a, RectangleF b)
    {
        if (!Finite(a) || !Finite(b)) return false;
        float minH = Math.Min(a.Height, b.Height), maxH = Math.Max(a.Height, b.Height);
        float overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (overlap < 0.5f * minH) return false;
        float gap = b.Left - a.Right;
        return gap <= 3 * maxH && gap >= -minH;
    }

    static RectangleF Union(OcrPage page, List<int> idx)
    {
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (int i in idx)
        {
            var x = page.Lines[i].Box;
            if (!float.IsFinite(x.Left) || !float.IsFinite(x.Top) || !float.IsFinite(x.Right) || !float.IsFinite(x.Bottom)) continue;
            l = Math.Min(l, x.Left); t = Math.Min(t, x.Top); r = Math.Max(r, x.Right); b = Math.Max(b, x.Bottom);
        }
        return l > r ? RectangleF.Empty : new RectangleF(l, t, r - l, b - t);
    }
}
