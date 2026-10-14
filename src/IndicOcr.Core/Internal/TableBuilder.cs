using System.Drawing;
using SkiaSharp;

namespace IndicOcr.Core.Internal;

/// <summary>Combines ruled and aligned detection with the page's line texts into <see cref="OcrTable"/> values.</summary>
internal static class TableBuilder
{
    /// <summary>readCell reads the text inside an image rectangle; null disables re-reading.</summary>
    public static IReadOnlyList<OcrTable> Build(SKBitmap image, OcrPage page, Func<RectangleF, string>? readCell)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(page);
        if (Math.Abs(image.Width - page.Width) > 1 || Math.Abs(image.Height - page.Height) > 1)
            throw new ArgumentException($"The bitmap is {image.Width}x{image.Height} but the page was recognised at {page.Width}x{page.Height}. " +
                "Pass the bitmap the boxes refer to (the upright copy from OcrEngine.RecognizeOriented when the page was rotated).", nameof(image));
        if (page.Lines.Count == 0) return Array.Empty<OcrTable>();

        var mask = InkMask.From(image);
        float tol = 4 * mask.Factor;
        var tables = new List<OcrTable>();
        var remaining = page.Lines.ToList();

        foreach (var rt in TableGrid.Detect(mask))
        {
            var area = RectangleF.Inflate(rt.Bounds, 2 * tol, 2 * tol);
            var inside = remaining.Where(l => area.Contains(Center(l.Box))).ToList();
            var t = BuildRuled(rt, inside, mask, readCell);
            if (t == null) continue; // not really a table (e.g. a framed paragraph): its lines stay available to the alignment path
            remaining.RemoveAll(l => inside.Contains(l));
            tables.Add(t);
        }

        foreach (var at in TableAlign.Detect(remaining, mask))
            tables.Add(BuildAligned(at, readCell));

        return tables.OrderBy(t => t.Bounds.Top).ThenBy(t => t.Bounds.Left).ToList();
    }


    static PointF Center(RectangleF r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    static OcrTable? BuildRuled(RuledTable rt, List<OcrLine> lines, InkMask mask, Func<RectangleF, string>? readCell)
    {
        var parts = rt.Groups.Select(_ => new List<(RectangleF Box, string Text)>()).ToList();
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l.Text)) continue;
            float cy = l.Box.Top + l.Box.Height / 2;
            var hits = new List<(int G, float Portion)>();
            for (int g = 0; g < rt.Groups.Count; g++)
            {
                var gb = rt.Groups[g].Box;
                if (cy < gb.Top || cy > gb.Bottom) continue;
                float ov = Math.Min(l.Box.Right, gb.Right) - Math.Max(l.Box.Left, gb.Left);
                if (ov > 0) hits.Add((g, ov / l.Box.Width));
            }
            if (hits.Count == 0)
            {   // centre on a border line: take the group with the largest overlap
                int best = -1; float bestA = 0;
                for (int g = 0; g < rt.Groups.Count; g++)
                {
                    var o = RectangleF.Intersect(rt.Groups[g].Box, l.Box);
                    float a = o.Width * o.Height;
                    if (a > bestA) { bestA = a; best = g; }
                }
                if (best >= 0) parts[best].Add((l.Box, l.Text));
                continue;
            }
            var real = hits.Where(h => h.Portion >= 0.1f && HasInk(mask, Crop(rt.Groups[h.G].Box, l.Box), l.Box.Height)).ToList();
            if (real.Count >= 2 && readCell != null)
            {
                foreach (var h in real)
                {
                    var crop = Crop(rt.Groups[h.G].Box, l.Box);
                    var text = readCell(crop).Trim();
                    if (text.Length > 0) parts[h.G].Add((crop, text));
                }
            }
            else
            {
                int g = (real.Count > 0 ? real : hits).OrderByDescending(h => h.Portion).First().G;
                parts[g].Add((l.Box, l.Text));
            }
        }

        var cells = new List<OcrTableCell>();
        int nonEmpty = 0;
        for (int g = 0; g < rt.Groups.Count; g++)
        {
            var grp = rt.Groups[g];
            string text = JoinParts(parts[g]);
            if (text.Length > 0) nonEmpty++;
            cells.Add(new OcrTableCell(grp.Row, grp.Col, grp.RowSpan, grp.ColSpan, text, grp.Box));
        }
        if (nonEmpty < 2) return null;
        // A single column is a table only when its cells are short (a list of items); a frame with a rule under the title around
        // paragraphs of text is a certificate or a boxed note, not a table.
        if (rt.Cols < 2 && cells.Any(c => LongestLine(c.Text) > MaxSingleColumnCellChars)) return null;
        return new OcrTable(rt.Bounds, rt.Rows, rt.Cols, cells) { HasRulings = true };
    }

    /// <summary>Longest line of a one-column ruled table's cells for it still to count as a table (about 3 words of a heading or an item name).</summary>
    const int MaxSingleColumnCellChars = 40;

    static int LongestLine(string text) => text.Length == 0 ? 0 : text.Split('\n').Max(l => l.Length);

    /// <summary>The part of the cell that the line occupies vertically, inset from the borders.</summary>
    static RectangleF Crop(RectangleF cell, RectangleF line)
    {
        float x0 = cell.Left + 3, x1 = cell.Right - 3;
        x0 = Math.Max(x0, line.Left - 2); x1 = Math.Min(x1, line.Right + 2);
        float y0 = Math.Max(line.Top, cell.Top + 2), y1 = Math.Min(line.Bottom, cell.Bottom - 2);
        return new RectangleF(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    static bool HasInk(InkMask m, RectangleF r, float lineH)
    {
        m.ColumnInk(r.Left, r.Right, r.Top, r.Bottom, out _, out var cols);
        return cols.Count(c => c) * m.Factor >= 0.3f * lineH;
    }

    /// <summary>Texts in reading order: parts on the same visual row are joined with a space, rows with a newline.</summary>
    static string JoinParts(List<(RectangleF Box, string Text)> parts)
    {
        if (parts.Count == 0) return "";
        var sorted = parts.OrderBy(p => p.Box.Top + p.Box.Height / 2).ToList();
        var sb = new System.Text.StringBuilder();
        RectangleF prev = default;
        var rowParts = new List<(RectangleF Box, string Text)>();
        void Flush()
        {
            if (rowParts.Count == 0) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(string.Join(" ", rowParts.OrderBy(p => p.Box.Left).Select(p => p.Text.Trim())));
            rowParts.Clear();
        }
        foreach (var p in sorted)
        {
            if (rowParts.Count > 0)
            {
                float overlap = Math.Min(prev.Bottom, p.Box.Bottom) - Math.Max(prev.Top, p.Box.Top);
                if (overlap <= 0.5f * Math.Min(prev.Height, p.Box.Height)) Flush();
            }
            rowParts.Add(p);
            prev = p.Box;
        }
        Flush();
        return sb.ToString();
    }

    static OcrTable BuildAligned(AlignedTable at, Func<RectangleF, string>? readCell)
    {
        var cells = new List<OcrTableCell>();
        foreach (var c in at.Cells)
        {
            var parts = new List<(RectangleF Box, string Text)>();
            foreach (var f in c.Frags)
            {
                string text;
                if (f.Whole) text = f.Line.Text;
                else if (readCell != null)
                {
                    var box = RectangleF.Intersect(f.Box, f.Line.Box);
                    text = readCell(box.IsEmpty ? f.Box : box);
                }
                else text = f.Index == 0 ? f.Line.Text : ""; // cannot split the text without reading again
                if (!string.IsNullOrWhiteSpace(text)) parts.Add((f.Box, text));
            }
            cells.Add(new OcrTableCell(c.Row, c.Col, 1, 1, JoinParts(parts), c.Box));
        }
        return new OcrTable(at.Bounds, at.Rows, at.Cols, cells) { HasRulings = false };
    }
}
