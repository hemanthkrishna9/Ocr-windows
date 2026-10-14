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
    /// the boxes span the gap, and the gap is wider than two line heights. Null when the page is single-column.
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
            if (spanN < bestSpan || (spanN == bestSpan && hi - lo > bestW)) { bestSpan = spanN; bestW = hi - lo; best = (lo, hi); }
        }
        return best;
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
