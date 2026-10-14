namespace IndicOcr.Core.Internal;

internal readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

internal static class Geometry
{
    static double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>Andrew monotone chain convex hull, counter-clockwise (in a y-up frame), no repeated end point.</summary>
    public static List<Pt> ConvexHull(IEnumerable<Pt> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        var h = new List<Pt>(pts.Count * 2);
        foreach (var p in pts)
        {
            while (h.Count >= 2 && Cross(h[^2], h[^1], p) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(p);
        }
        int lower = h.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (h.Count >= lower && Cross(h[^2], h[^1], p) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(p);
        }
        h.RemoveAt(h.Count - 1);
        return h;
    }

    /// <summary>
    /// Minimum-area enclosing rectangle of a convex hull. Uses the rotating-calipers theorem
    /// (an optimal rectangle has a side collinear with a hull edge) and tests every hull edge.
    /// Returns the 4 corners (unordered orientation).
    /// </summary>
    public static Pt[] MinAreaRect(IReadOnlyList<Pt> hull)
    {
        if (hull.Count == 0) return new Pt[4];
        if (hull.Count == 1) return new[] { hull[0], hull[0], hull[0], hull[0] };
        double bestArea = double.MaxValue;
        Pt[] best = new Pt[4];
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            var e = b - a;
            double len = e.Length;
            if (len < 1e-12) continue;
            var u = new Pt(e.X / len, e.Y / len);
            var n = new Pt(-u.Y, u.X);
            double minU = double.MaxValue, maxU = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
            foreach (var p in hull)
            {
                double pu = (p.X - a.X) * u.X + (p.Y - a.Y) * u.Y;
                double pn = (p.X - a.X) * n.X + (p.Y - a.Y) * n.Y;
                if (pu < minU) minU = pu;
                if (pu > maxU) maxU = pu;
                if (pn < minN) minN = pn;
                if (pn > maxN) maxN = pn;
            }
            double area = (maxU - minU) * (maxN - minN);
            if (area < bestArea)
            {
                bestArea = area;
                Pt C(double pu, double pn) => new(a.X + u.X * pu + n.X * pn, a.Y + u.Y * pu + n.Y * pn);
                best = new[] { C(minU, minN), C(maxU, minN), C(maxU, maxN), C(minU, maxN) };
            }
        }
        return best;
    }

    /// <summary>
    /// Orders 4 rectangle corners as top-left, top-right, bottom-right, bottom-left (PaddleOCR get_mini_boxes rule)
    /// and returns the shorter side length.
    /// </summary>
    public static (Pt[] Box, double ShortSide) OrderBox(Pt[] r)
    {
        var s = r.OrderBy(p => p.X).ToArray();
        Pt tl, bl, tr, br;
        if (s[0].Y <= s[1].Y) { tl = s[0]; bl = s[1]; } else { tl = s[1]; bl = s[0]; }
        if (s[2].Y <= s[3].Y) { tr = s[2]; br = s[3]; } else { tr = s[3]; br = s[2]; }
        double w = (tr - tl).Length, h = (bl - tl).Length;
        return (new[] { tl, tr, br, bl }, Math.Min(w, h));
    }

    public static double PolygonArea(IReadOnlyList<Pt> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var q = p[(i + 1) % p.Count];
            a += p[i].X * q.Y - q.X * p[i].Y;
        }
        return Math.Abs(a) / 2;
    }

    public static double Perimeter(IReadOnlyList<Pt> p)
    {
        double l = 0;
        for (int i = 0; i < p.Count; i++) l += (p[(i + 1) % p.Count] - p[i]).Length;
        return l;
    }

    /// <summary>
    /// DB unclip. Distance = area * ratio / perimeter. For a rectangle the offset polygon's minimum-area
    /// rectangle is the rectangle grown by that distance on every side, so no polygon clipper is needed.
    /// Input is an ordered box (tl,tr,br,bl).
    /// </summary>
    public static Pt[] Unclip(Pt[] box, double ratio)
    {
        double area = PolygonArea(box), per = Perimeter(box);
        if (per < 1e-9) return box;
        double d = area * ratio / per;
        var ex = box[1] - box[0];
        var ey = box[3] - box[0];
        double lx = ex.Length, ly = ey.Length;
        if (lx < 1e-9 || ly < 1e-9) return box;
        ex = ex * (1 / lx);
        ey = ey * (1 / ly);
        return new[]
        {
            box[0] - ex * d - ey * d,
            box[1] + ex * d - ey * d,
            box[2] + ex * d + ey * d,
            box[3] - ex * d + ey * d,
        };
    }

    /// <summary>Point-in-convex-quad test with tolerance (works for either winding).</summary>
    public static bool InsideConvex(Pt[] q, double x, double y, double eps)
    {
        bool pos = false, neg = false;
        for (int i = 0; i < q.Length; i++)
        {
            var a = q[i];
            var b = q[(i + 1) % q.Length];
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-12) continue;
            double c = (ex * (y - a.Y) - ey * (x - a.X)) / len; // signed distance
            if (c > eps) pos = true;
            else if (c < -eps) neg = true;
            if (pos && neg) return false;
        }
        return true;
    }
}
