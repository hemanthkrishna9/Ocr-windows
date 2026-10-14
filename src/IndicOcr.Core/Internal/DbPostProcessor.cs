namespace IndicOcr.Core.Internal;

internal sealed record DbParams
{
    public float BinaryThreshold { get; init; } = 0.3f;
    public float BoxThreshold { get; init; } = 0.6f;
    public double UnclipRatio { get; init; } = 1.5;
    public double MinSize { get; init; } = 3;
    public int MaxCandidates { get; init; } = 1000;
}

/// <summary>PaddleOCR DBPostProcess: binarise, connected components, min-area rects, score, unclip, map back.</summary>
internal static class DbPostProcessor
{
    /// <summary>
    /// prob: row-major [h*w] probability map. Returns ordered quads (tl,tr,br,bl) in source-image coordinates
    /// (mapped by srcW/w and srcH/h) with their scores.
    /// </summary>
    public static List<(Pt[] Box, float Score)> Extract(float[] prob, int w, int h, int srcW, int srcH, DbParams p)
    {
        var result = new List<(Pt[], float)>();
        var visited = new bool[w * h];
        var stack = new int[Math.Max(16, w * h / 4)];
        var members = new List<int>();
        double sx = (double)srcW / w, sy = (double)srcH / h;
        int candidates = 0;

        for (int y0 = 0; y0 < h; y0++)
        {
            for (int x0 = 0; x0 < w; x0++)
            {
                int i0 = y0 * w + x0;
                if (visited[i0] || prob[i0] <= p.BinaryThreshold) continue;

                // 8-connected flood fill
                members.Clear();
                int sp = 0;
                stack[sp++] = i0;
                visited[i0] = true;
                int minY = y0, maxY = y0;
                while (sp > 0)
                {
                    int idx = stack[--sp];
                    members.Add(idx);
                    int cy = idx / w, cx = idx - cy * w;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = cy + dy;
                        if (ny < 0 || ny >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx;
                            if (nx < 0 || nx >= w || (dx == 0 && dy == 0)) continue;
                            int ni = ny * w + nx;
                            if (visited[ni] || prob[ni] <= p.BinaryThreshold) continue;
                            visited[ni] = true;
                            if (sp == stack.Length) Array.Resize(ref stack, stack.Length * 2);
                            stack[sp++] = ni;
                        }
                    }
                }

                if (++candidates > p.MaxCandidates) return result;
                if (members.Count < 4) continue;

                // Hull of a pixel set == hull of its per-row extremes.
                int rows = maxY - minY + 1;
                var rowMin = new int[rows];
                var rowMax = new int[rows];
                Array.Fill(rowMin, int.MaxValue);
                Array.Fill(rowMax, int.MinValue);
                foreach (int idx in members)
                {
                    int cy = idx / w, cx = idx - cy * w;
                    int r = cy - minY;
                    if (cx < rowMin[r]) rowMin[r] = cx;
                    if (cx > rowMax[r]) rowMax[r] = cx;
                }
                var ext = new List<Pt>(rows * 2);
                for (int r = 0; r < rows; r++)
                {
                    if (rowMin[r] == int.MaxValue) continue;
                    ext.Add(new Pt(rowMin[r], r + minY));
                    ext.Add(new Pt(rowMax[r], r + minY));
                }
                var hull = Geometry.ConvexHull(ext);
                var (box, sside) = Geometry.OrderBox(Geometry.MinAreaRect(hull));
                if (sside < p.MinSize) continue;

                float score = BoxScore(prob, w, h, box);
                if (score < p.BoxThreshold) continue;

                var grown = Geometry.Unclip(box, p.UnclipRatio);
                var (box2, sside2) = Geometry.OrderBox(grown);
                if (sside2 < p.MinSize + 2) continue;

                var mapped = new Pt[4];
                for (int k = 0; k < 4; k++)
                {
                    mapped[k] = new Pt(
                        Math.Clamp(Math.Round(box2[k].X * sx), 0, srcW),
                        Math.Clamp(Math.Round(box2[k].Y * sy), 0, srcH));
                }
                // PaddleOCR filter_tag_det_res: drop boxes with a side <= 3 px in the source image
                if ((mapped[1] - mapped[0]).Length <= 3 || (mapped[3] - mapped[0]).Length <= 3) continue;
                result.Add((mapped, score));
            }
        }
        return result;
    }

    /// <summary>Mean probability inside the (rotated) box, as PaddleOCR box_score_fast.</summary>
    public static float BoxScore(float[] prob, int w, int h, Pt[] box)
    {
        int xmin = Math.Clamp((int)Math.Floor(box.Min(b => b.X)), 0, w - 1);
        int xmax = Math.Clamp((int)Math.Ceiling(box.Max(b => b.X)), 0, w - 1);
        int ymin = Math.Clamp((int)Math.Floor(box.Min(b => b.Y)), 0, h - 1);
        int ymax = Math.Clamp((int)Math.Ceiling(box.Max(b => b.Y)), 0, h - 1);
        double sum = 0;
        int n = 0;
        for (int y = ymin; y <= ymax; y++)
            for (int x = xmin; x <= xmax; x++)
                if (Geometry.InsideConvex(box, x, y, 0.25))
                {
                    sum += prob[y * w + x];
                    n++;
                }
        return n == 0 ? 0 : (float)(sum / n);
    }
}
