using System.Drawing;

namespace IndicOcr.Core.Internal;

internal static class LineOrdering
{
    /// <summary>
    /// Orders boxes top-to-bottom, left-to-right. Boxes are grouped into one row when their vertical
    /// overlap exceeds half of the smaller height. A page with two text columns (a clear vertical gap that
    /// most lines respect) is read column by column; lines that span the gap (headings) split the page into bands.
    /// </summary>
    public static List<T> Order<T>(IEnumerable<T> items, Func<T, RectangleF> box)
    {
        var all = items.Select(i => (Item: i, R: box(i))).ToList();
        var gap = FindColumnGap(all.Select(a => a.R).ToList());
        if (gap == null) return OrderRows(all);

        var (lo, hi) = gap.Value;
        var result = new List<T>(all.Count);
        var left = new List<(T Item, RectangleF R)>();
        var right = new List<(T Item, RectangleF R)>();
        void Flush()
        {
            result.AddRange(OrderRows(left));
            result.AddRange(OrderRows(right));
            left.Clear(); right.Clear();
        }
        foreach (var t in all.OrderBy(a => a.R.Top + a.R.Height / 2))
        {
            if (t.R.Right <= lo) left.Add(t);
            else if (t.R.Left >= hi) right.Add(t);
            else { Flush(); result.Add(t.Item); } // spans the gap: a full-width line
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Finds a vertical gap [lo, hi] splitting the boxes into a left and a right column: both columns have at least 3 lines
    /// that are wide (a fifth of the text width or more, so label/value tables are not mistaken for columns), at most 15% of
    /// the boxes span the gap, and the gap is wider than two line heights. A label/value form is not a column layout: when most left
    /// lines have a right partner on the same row AND the strip between them is much wider than the left text (short labels, a big empty strip,
    /// then the values) the page is read row by row. Prose columns have a gutter about as wide as their text or less, so they pass.
    /// Null when the page is single-column.
    /// </summary>
    internal static (float Lo, float Hi)? FindColumnGap(IReadOnlyList<RectangleF> rects)
    {
        int n = rects.Count;
        if (n < 8) return null;
        float minX = rects.Min(r => r.Left), maxX = rects.Max(r => r.Right);
        float width = maxX - minX;
        if (width <= 0) return null;
        float medH = rects.Select(r => r.Height).OrderBy(h => h).ElementAt(n / 2);
        float minGap = Math.Max(2 * medH, 0.02f * width);

        // Candidate gaps lie between a box's right edge and some other box's left edge: sweep sorted edges.
        var rights = rects.Select(r => r.Right).OrderBy(x => x).Distinct().ToList();
        (float Lo, float Hi)? best = null;
        float bestW = 0;
        int bestSpan = int.MaxValue; // fewest spanning lines first (they are headings), then the widest gap
        foreach (float lo in rights)
        {
            if (lo >= maxX - minGap || lo <= minX + minGap) continue;
            // smallest Left >= lo among boxes that start right of the gap
            float hi = float.MaxValue;
            foreach (var r in rects) if (r.Left >= lo && r.Left < hi) hi = r.Left;
            if (hi == float.MaxValue || hi - lo < minGap) continue;
            int leftN = 0, rightN = 0, spanN = 0;
            var leftW = new List<float>(); var rightW = new List<float>();
            foreach (var r in rects)
            {
                if (r.Right <= lo) { leftN++; leftW.Add(r.Width); }
                else if (r.Left >= hi) { rightN++; rightW.Add(r.Width); }
                else spanN++;
            }
            if (leftN < 3 || rightN < 3 || spanN > 0.15 * n) continue;
            if (Median(leftW) < 0.2f * width || Median(rightW) < 0.2f * width) continue;
            if (LooksLikeForm(rects, lo, hi)) continue;
            if (spanN < bestSpan || (spanN == bestSpan && hi - lo > bestW)) { bestSpan = spanN; bestW = hi - lo; best = (lo, hi); }
        }
        return best;
    }

    /// <summary>
    /// Row-aligned label/value pairs: at least 60% of the left lines share a row with a right line, and the empty strip between the
    /// left text and the right lines is wider than the left text itself (measured strip / text: forms 1.3 to 1.9, prose columns 0.6).
    /// The left text's end ignores lines more than 1.5 times as wide as the median (a heading above the labels).
    /// </summary>
    static bool LooksLikeForm(IReadOnlyList<RectangleF> rects, float lo, float hi)
    {
        var left = rects.Where(r => r.Right <= lo).ToList();
        var right = rects.Where(r => r.Left >= hi).ToList();
        if (left.Count == 0 || right.Count == 0) return false;
        float medW = Median(left.Select(r => r.Width).ToList());
        var body = left.Where(r => r.Width <= 1.5f * medW).ToList(); // leaves out a heading much longer than the labels
        float textEnd = body.Max(r => r.Right);
        float extent = textEnd - body.Min(r => r.Left);
        if (hi - textEnd <= 1.0f * extent) return false;
        int paired = 0;
        foreach (var l in left)
            foreach (var r in right)
            {
                float overlap = Math.Min(l.Bottom, r.Bottom) - Math.Max(l.Top, r.Top);
                if (overlap > 0.5f * Math.Min(l.Height, r.Height)) { paired++; break; }
            }
        return paired >= 0.6 * left.Count;
    }

    static float Median(List<float> v) { v.Sort(); return v[v.Count / 2]; }

    static List<T> OrderRows<T>(List<(T Item, RectangleF R)> items)
    {
        var sorted = items.OrderBy(t => t.R.Top + t.R.Height / 2).ToList();
        var rows = new List<(float Top, float Bottom, int N, List<(T Item, RectangleF R)> Members)>();
        foreach (var t in sorted)
        {
            int placed = -1;
            for (int k = rows.Count - 1; k >= 0 && k >= rows.Count - 2; k--)
            {
                var row = rows[k];
                float overlap = Math.Min(row.Bottom, t.R.Bottom) - Math.Max(row.Top, t.R.Top);
                float minH = Math.Min(row.Bottom - row.Top, t.R.Height);
                if (minH > 0 && overlap > 0.5f * minH) { placed = k; break; }
            }
            if (placed < 0)
            {
                rows.Add((t.R.Top, t.R.Bottom, 1, new() { t }));
            }
            else
            {
                var row = rows[placed];
                row.Members.Add(t);
                int n = row.N + 1;
                rows[placed] = ((row.Top * row.N + t.R.Top) / n, (row.Bottom * row.N + t.R.Bottom) / n, n, row.Members);
            }
        }
        return rows
            .OrderBy(r => (r.Top + r.Bottom) / 2)
            .SelectMany(r => r.Members.OrderBy(m => m.R.Left).Select(m => m.Item))
            .ToList();
    }
}
