using System.Drawing;

namespace IndicOcr.Core.Internal;

/// <summary>A ruled table: grid lines in page pixels and the cell groups (a merged cell is one group).</summary>
internal sealed class RuledTable
{
    public required float[] Xs { get; init; }
    public required float[] Ys { get; init; }
    public required List<CellGroup> Groups { get; init; }
    public RectangleF Bounds => new(Xs[0], Ys[0], Xs[^1] - Xs[0], Ys[^1] - Ys[0]);
    public int Rows => Ys.Length - 1;
    public int Cols => Xs.Length - 1;
}

internal sealed record CellGroup(int Row, int Col, int RowSpan, int ColSpan, RectangleF Box);

/// <summary>
/// Finds bordered tables. Long dark horizontal and vertical runs are taken from the reduced ink mask; lines that touch form one component;
/// a component with at least two lines each way is a table. A wall between two grid cells exists when a line covers most of that edge;
/// cells without a wall between them are merged (only when the merged block is a rectangle).
/// </summary>
internal static class TableGrid
{
    internal readonly record struct Seg(float Pos, float A, float B);

    public static List<RuledTable> Detect(InkMask m)
    {
        int f = m.Factor;
        var hs = FindLines(m.Data, m.W, m.H, f);
        var tr = Transpose(m.Data, m.W, m.H);
        var vs = FindLines(tr, m.H, m.W, f);
        if (hs.Count < 2 || vs.Count < 2) return new();

        float tol = 4 * f;
        // union-find over touching lines (H indices first, then V)
        int n = hs.Count + vs.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i < hs.Count; i++)
            for (int j = 0; j < vs.Count; j++)
            {
                var h = hs[i]; var v = vs[j];
                if (v.Pos >= h.A - tol && v.Pos <= h.B + tol && h.Pos >= v.A - tol && h.Pos <= v.B + tol)
                    parent[Find(i)] = Find(hs.Count + j);
            }

        var result = new List<RuledTable>();
        foreach (var comp in Enumerable.Range(0, n).GroupBy(Find))
        {
            var ch = comp.Where(i => i < hs.Count).Select(i => hs[i]).ToList();
            var cv = comp.Where(i => i >= hs.Count).Select(i => vs[i - hs.Count]).ToList();
            if (ch.Count < 2 || cv.Count < 2) continue;
            var t = BuildGrid(ch, cv, tol);
            if (t != null) result.Add(t);
        }
        return result.OrderBy(t => t.Bounds.Top).ThenBy(t => t.Bounds.Left).ToList();
    }

    static RuledTable? BuildGrid(List<Seg> hs, List<Seg> vs, float tol)
    {
        var ys = Cluster(hs.Select(h => h.Pos), tol);
        var xs = Cluster(vs.Select(v => v.Pos), tol);
        if (ys.Length < 2 || xs.Length < 2) return null;
        int R = ys.Length - 1, C = xs.Length - 1;
        if (R * C < 2) return null;

        bool WallV(int c, int r) => Coverage(vs, xs[c], ys[r], ys[r + 1], tol) >= 0.6f;   // wall on the left edge of column c (c in 0..C), row r
        bool WallH(int r, int c) => Coverage(hs, ys[r], xs[c], xs[c + 1], tol) >= 0.6f;   // wall on the top edge of row r (r in 0..R), column c

        var parent = Enumerable.Range(0, R * C).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int r = 0; r < R; r++)
            for (int c = 0; c < C; c++)
            {
                if (c + 1 < C && !WallV(c + 1, r)) parent[Find(r * C + c)] = Find(r * C + c + 1);
                if (r + 1 < R && !WallH(r + 1, c)) parent[Find(r * C + c)] = Find((r + 1) * C + c);
            }

        var groups = new List<CellGroup>();
        foreach (var g in Enumerable.Range(0, R * C).GroupBy(Find))
        {
            var cells = g.Select(i => (R: i / C, C: i % C)).ToList();
            int r0 = cells.Min(x => x.R), r1 = cells.Max(x => x.R), c0 = cells.Min(x => x.C), c1 = cells.Max(x => x.C);
            if ((r1 - r0 + 1) * (c1 - c0 + 1) == cells.Count)
                groups.Add(Group(r0, c0, r1, c1, xs, ys));
            else // not a rectangle: the borders are too broken to trust a merge, keep single cells
                foreach (var x in cells) groups.Add(Group(x.R, x.C, x.R, x.C, xs, ys));
        }
        return new RuledTable { Xs = xs, Ys = ys, Groups = groups.OrderBy(g => g.Row).ThenBy(g => g.Col).ToList() };
    }

    static CellGroup Group(int r0, int c0, int r1, int c1, float[] xs, float[] ys) =>
        new(r0, c0, r1 - r0 + 1, c1 - c0 + 1, new RectangleF(xs[c0], ys[r0], xs[c1 + 1] - xs[c0], ys[r1 + 1] - ys[r0]));

    /// <summary>Fraction of [a,b] covered by segments lying within tol of pos.</summary>
    static float Coverage(List<Seg> segs, float pos, float a, float b, float tol)
    {
        var iv = new List<(float, float)>();
        foreach (var s in segs)
        {
            if (Math.Abs(s.Pos - pos) > tol) continue;
            float lo = Math.Max(a, s.A), hi = Math.Min(b, s.B);
            if (hi > lo) iv.Add((lo, hi));
        }
        if (iv.Count == 0 || b <= a) return 0;
        iv.Sort();
        float covered = 0, curLo = iv[0].Item1, curHi = iv[0].Item2;
        foreach (var (lo, hi) in iv.Skip(1))
        {
            if (lo <= curHi) curHi = Math.Max(curHi, hi);
            else { covered += curHi - curLo; curLo = lo; curHi = hi; }
        }
        covered += curHi - curLo;
        // a line that stops a few pixels short of the corner still counts as the whole edge
        return Math.Min(1f, (covered + 2 * tol) / (b - a));
    }

    static float[] Cluster(IEnumerable<float> values, float tol)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var res = new List<float>();
        var cur = new List<float>();
        foreach (var v in sorted)
        {
            if (cur.Count > 0 && v - cur[^1] > tol) { res.Add(cur.Average()); cur.Clear(); }
            cur.Add(v);
        }
        if (cur.Count > 0) res.Add(cur.Average());
        return res.ToArray();
    }

    static bool[] Transpose(bool[] d, int w, int h)
    {
        var t = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) t[x * h + y] = d[y * w + x];
        return t;
    }

    /// <summary>
    /// Long thin dark runs along the rows of a w x h mask (pass the transposed mask for vertical lines). Positions are returned in image pixels
    /// (mask pixel centre times factor). Runs in neighbouring rows that overlap are one line; lines thicker than 10 mask pixels (filled blocks) are dropped.
    /// </summary>
    static List<Seg> FindLines(bool[] d, int w, int h, int f)
    {
        int minLen = Math.Max(24, (int)(0.025 * w));
        const int gapTol = 2, maxThick = 10;
        var open = new List<(int First, int Last, int A, int B)>();
        var done = new List<(int First, int Last, int A, int B)>();
        for (int y = 0; y < h; y++)
        {
            var runs = new List<(int A, int B)>();
            int o = y * w, x = 0;
            while (x < w)
            {
                if (!d[o + x]) { x++; continue; }
                int a = x, b = x, gap = 0;
                while (x < w)
                {
                    if (d[o + x]) { b = x; gap = 0; }
                    else if (++gap > gapTol) break;
                    x++;
                }
                if (b - a + 1 >= minLen) runs.Add((a, b));
            }
            var next = new List<(int First, int Last, int A, int B)>();
            var used = new bool[open.Count];
            foreach (var (a, b) in runs)
            {
                int hit = -1;
                for (int k = 0; k < open.Count; k++)
                    if (!used[k] && open[k].Last == y - 1 && Math.Min(b, open[k].B) - Math.Max(a, open[k].A) >= 0.5 * Math.Min(b - a, open[k].B - open[k].A)) { hit = k; break; }
                if (hit >= 0) { used[hit] = true; var o2 = open[hit]; next.Add((o2.First, y, Math.Min(a, o2.A), Math.Max(b, o2.B))); }
                else next.Add((y, y, a, b));
            }
            for (int k = 0; k < open.Count; k++) if (!used[k]) done.Add(open[k]);
            open = next;
        }
        done.AddRange(open);

        var segs = done.Where(s => s.Last - s.First + 1 <= maxThick)
            .Select(s => new Seg(((s.First + s.Last) / 2f + 0.5f) * f, s.A * f, (s.B + 1) * f)).ToList();
        // join collinear pieces (a line interrupted by a gap or by anti-aliasing)
        segs = segs.OrderBy(s => s.Pos).ThenBy(s => s.A).ToList();
        var merged = new List<Seg>();
        foreach (var s in segs)
        {
            int k = merged.FindIndex(m => Math.Abs(m.Pos - s.Pos) <= 3 * f && s.A <= m.B + 8 * f && s.B >= m.A - 8 * f);
            if (k >= 0) { var m = merged[k]; merged[k] = new Seg((m.Pos + s.Pos) / 2, Math.Min(m.A, s.A), Math.Max(m.B, s.B)); }
            else merged.Add(s);
        }
        return merged;
    }
}
