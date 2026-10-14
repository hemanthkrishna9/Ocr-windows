using System.Drawing;

namespace IndicOcr.Core.Internal;

internal static class LineOrdering
{
    /// <summary>
    /// Orders boxes top-to-bottom, left-to-right. Boxes are grouped into one row when their vertical
    /// overlap exceeds half of the smaller height.
    /// </summary>
    public static List<T> Order<T>(IEnumerable<T> items, Func<T, RectangleF> box)
    {
        var sorted = items.Select(i => (Item: i, R: box(i))).OrderBy(t => t.R.Top + t.R.Height / 2).ToList();
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
