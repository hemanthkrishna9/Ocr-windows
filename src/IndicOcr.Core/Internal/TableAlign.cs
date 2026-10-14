using System.Drawing;
using System.Text.RegularExpressions;

namespace IndicOcr.Core.Internal;

/// <summary>A piece of an OCR line: the whole line, or one part of it after splitting at a wide internal gap.</summary>
internal sealed class Frag
{
    public required OcrLine Line { get; init; }
    public required RectangleF Box { get; init; }
    /// <summary>Position of this piece within its line (left to right) and the number of pieces.</summary>
    public int Index { get; init; }
    public int Count { get; init; } = 1;
    public bool Whole => Count == 1;
}

internal sealed class AlignedCell
{
    public required int Row { get; init; }
    public required int Col { get; init; }
    public required RectangleF Box { get; init; }
    public required List<Frag> Frags { get; init; }
}

internal sealed class AlignedTable
{
    public required RectangleF Bounds { get; init; }
    public required int Rows { get; init; }
    public required int Cols { get; init; }
    public required List<AlignedCell> Cells { get; init; }
}

/// <summary>
/// Borderless tables and label/value forms from text alignment. Each OCR line is cut at wide empty gaps into pieces; pieces are grouped
/// into rows by vertical overlap; a table is a stretch of closely spaced rows with at least three rows holding two or more pieces and with
/// at least one empty vertical gutter that no piece of those rows crosses. Columns are the spaces between gutters, so left, right and
/// centre aligned columns all work. Rejected as not-a-table: prose in columns (every column wide in line heights), numbered paragraphs
/// (a two-column layout whose first column holds only list markers such as "1." or "(a)"), two columns of long text, letter signature
/// blocks ("Place: ..." on the left, a closing and name on the right), and two-column layouts of fewer than four rows unless both
/// columns hold short or numeric cells.
/// </summary>
internal static class TableAlign
{
    const float SplitGapInHeights = 0.9f;   // an empty gap this wide (in line heights) cuts a line into cells
    const float GutterInHeights = 0.8f;     // a gutter between columns must be at least this wide
    const float RowGapInHeights = 2.0f;     // larger vertical gaps end a table
    const int MinStrongRows = 3;

    sealed class Row
    {
        public List<Frag> Frags = new();
        public float Top, Bottom;
        public int N;
    }

    public static List<AlignedTable> Detect(IReadOnlyList<OcrLine> lines, InkMask mask)
    {
        var frags = new List<Frag>();
        foreach (var l in lines)
            if (l.Box.Width > 0 && l.Box.Height > 0 && !string.IsNullOrWhiteSpace(l.Text)) frags.AddRange(Split(l, mask));
        if (frags.Count < 2 * MinStrongRows) return new();
        float medH = frags.Select(f => f.Box.Height).OrderBy(x => x).ElementAt(frags.Count / 2);

        var rows = GroupRows(frags);
        var result = new List<AlignedTable>();
        foreach (var run in Runs(rows, medH))
        {
            var t = BuildTable(run, medH);
            if (t != null) result.Add(t);
        }
        return result;
    }

    static List<Frag> Split(OcrLine l, InkMask m)
    {
        var b = l.Box;
        var whole = new List<Frag> { new() { Line = l, Box = b } };
        if (b.Width < 3 * b.Height) return whole;
        m.ColumnInk(b.Left, b.Right, b.Top + 0.1f * b.Height, b.Bottom - 0.1f * b.Height, out int first, out var cols);
        float f = m.Factor;
        float minGap = SplitGapInHeights * b.Height;
        var runs = new List<(int A, int B)>();
        int i = 0;
        while (i < cols.Length)
        {
            if (!cols[i]) { i++; continue; }
            int a = i, e = i;
            while (i < cols.Length)
            {
                if (cols[i]) { e = i; i++; continue; }
                int g = i; while (g < cols.Length && !cols[g]) g++;
                if (g >= cols.Length || (g - i) * f >= minGap) break;
                i = g;
            }
            runs.Add((a, e));
            i = e + 1;
        }
        if (runs.Count < 2) return whole;
        var res = new List<Frag>();
        float pad = 0.15f * b.Height;
        for (int k = 0; k < runs.Count; k++)
        {
            float x0 = Math.Max(b.Left, (first + runs[k].A) * f - pad), x1 = Math.Min(b.Right, (first + runs[k].B + 1) * f + pad);
            if (x1 - x0 < 1) continue;
            res.Add(new Frag { Line = l, Box = new RectangleF(x0, b.Top, x1 - x0, b.Height), Index = k, Count = runs.Count });
        }
        return res.Count >= 2 ? res : whole;
    }

    static List<Row> GroupRows(List<Frag> frags)
    {
        var rows = new List<Row>();
        foreach (var fr in frags.OrderBy(f => f.Box.Top + f.Box.Height / 2))
        {
            Row? hit = null;
            for (int k = rows.Count - 1; k >= 0 && k >= rows.Count - 2; k--)
            {
                var r = rows[k];
                float overlap = Math.Min(r.Bottom, fr.Box.Bottom) - Math.Max(r.Top, fr.Box.Top);
                float minH = Math.Min(r.Bottom - r.Top, fr.Box.Height);
                if (minH > 0 && overlap > 0.5f * minH) { hit = r; break; }
            }
            if (hit == null) { hit = new Row { Top = fr.Box.Top, Bottom = fr.Box.Bottom }; rows.Add(hit); }
            else { hit.Top = (hit.Top * hit.N + fr.Box.Top) / (hit.N + 1); hit.Bottom = (hit.Bottom * hit.N + fr.Box.Bottom) / (hit.N + 1); }
            hit.Frags.Add(fr);
            hit.N++;
            if (hit.N == 1) { hit.Top = fr.Box.Top; hit.Bottom = fr.Box.Bottom; }
        }
        foreach (var r in rows) r.Frags.Sort((a, b) => a.Box.Left.CompareTo(b.Box.Left));
        return rows.OrderBy(r => r.Top + r.Bottom).ToList();
    }

    static IEnumerable<List<Row>> Runs(List<Row> rows, float medH)
    {
        List<Row>? cur = null;
        float maxGap = RowGapInHeights * medH;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            bool strong = r.Frags.Count >= 2;
            if (cur == null) { if (strong) cur = new() { r }; continue; }
            if (r.Top - cur[^1].Bottom > maxGap)
            {
                yield return cur;
                cur = strong ? new() { r } : null;
                continue;
            }
            if (strong) { cur.Add(r); continue; }
            // a one-piece row is kept only between table rows (a wrapped cell line, an empty cell)
            if (i + 1 < rows.Count && rows[i + 1].Frags.Count >= 2 && rows[i + 1].Top - r.Bottom <= maxGap) cur.Add(r);
            else { yield return cur; cur = null; }
        }
        if (cur != null) yield return cur;
    }

    static AlignedTable? BuildTable(List<Row> run, float medH)
    {
        var strong = run.Where(r => r.Frags.Count >= 2).ToList();
        if (strong.Count < MinStrongRows) return null;
        var sf = strong.SelectMany(r => r.Frags).ToList();

        // gutters: empty x-intervals between the pieces of all strong rows
        var iv = sf.Select(f => (L: f.Box.Left, R: f.Box.Right)).OrderBy(x => x.L).ToList();
        var gutters = new List<(float A, float B)>();
        float reach = iv[0].R;
        float minGutter = GutterInHeights * medH;
        foreach (var (l, r) in iv.Skip(1))
        {
            if (l - reach >= minGutter) gutters.Add((reach, l));
            reach = Math.Max(reach, r);
        }

        // a column that holds pieces of fewer than two strong rows is noise: fold it into a neighbour
        while (gutters.Count > 0)
        {
            var cols = Columns(gutters, iv[0].L, reach);
            int bad = -1;
            for (int c = 0; c < cols.Count; c++)
                if (strong.Count(r => r.Frags.Any(f => ColOf(gutters, f) == c)) < 2) { bad = c; break; }
            if (bad < 0) break;
            gutters.RemoveAt(bad == 0 ? 0 : bad - 1);
        }
        if (gutters.Count == 0) return null;
        int nCols = gutters.Count + 1;

        // at least three rows must really use two or more columns
        if (run.Count(r => r.Frags.Select(f => ColOf(gutters, f)).Distinct().Count() >= 2) < MinStrongRows) return null;

        if (nCols == 2 && !PlausibleTwoColumns(sf, gutters, strong.Count)) return null;

        // prose in columns: every column holds long lines
        bool prose = true;
        for (int c = 0; c < nCols && prose; c++)
        {
            var inCol = sf.Where(f => ColOf(gutters, f) == c).ToList();
            float w = inCol.Select(f => f.Box.Width).OrderBy(x => x).ElementAt(inCol.Count / 2);
            float h = inCol.Select(f => f.Box.Height).OrderBy(x => x).ElementAt(inCol.Count / 2);
            if (w / h < 12) prose = false;
        }
        if (prose) return null;

        var cells = new List<AlignedCell>();
        var colSpans = Columns(gutters, iv[0].L, reach);
        for (int r = 0; r < run.Count; r++)
        {
            float top = run[r].Frags.Min(f => f.Box.Top), bottom = run[r].Frags.Max(f => f.Box.Bottom);
            for (int c = 0; c < nCols; c++)
            {
                var fr = run[r].Frags.Where(f => ColOf(gutters, f) == c).ToList();
                RectangleF box;
                if (fr.Count > 0)
                {
                    float l = fr.Min(f => f.Box.Left), t = fr.Min(f => f.Box.Top), rr = fr.Max(f => f.Box.Right), bb = fr.Max(f => f.Box.Bottom);
                    box = RectangleF.FromLTRB(l, t, rr, bb);
                }
                else box = RectangleF.FromLTRB(colSpans[c].A, top, colSpans[c].B, bottom);
                cells.Add(new AlignedCell { Row = r, Col = c, Box = box, Frags = fr });
            }
        }
        var all = run.SelectMany(r => r.Frags).ToList();
        var bounds = RectangleF.FromLTRB(all.Min(f => f.Box.Left), all.Min(f => f.Box.Top), all.Max(f => f.Box.Right), all.Max(f => f.Box.Bottom));
        return new AlignedTable { Bounds = bounds, Rows = run.Count, Cols = nCols, Cells = cells };
    }

    static readonly Regex ListMarker = new(@"^\(?[\p{Nd}a-zA-Z ]{1,4}[.)]$|^[-\u2022\u00B7\u25AA\u25CF\u25CB\u2013*]$", RegexOptions.CultureInvariant);
    static readonly Regex Numeric = new(@"^[\p{Nd}\p{Sc}.,:/%+\-() ]+$", RegexOptions.CultureInvariant);

    /// <summary>A cell that is a number, date or short word: at most 8 line heights wide (about 18 Latin characters) or made of digits and punctuation.</summary>
    static bool ShortOrNumeric(Frag f) =>
        f.Box.Width <= 8 * f.Box.Height || Numeric.IsMatch(f.Line.Text.Trim()) && f.Whole;

    /// <summary>
    /// Extra checks for a two-column candidate, where false positives are common (lists, letters). Numbered paragraphs, a "Label: value"
    /// column next to a plain closing column (signature block), two columns of long text and short two-column layouts with only three
    /// rows are not tables.
    /// </summary>
    static bool PlausibleTwoColumns(List<Frag> strongFrags, List<(float A, float B)> gutters, int strongRows)
    {
        var c0 = strongFrags.Where(f => ColOf(gutters, f) == 0).ToList();
        var c1 = strongFrags.Where(f => ColOf(gutters, f) == 1).ToList();
        if (c0.Count == 0 || c1.Count == 0) return false;
        // numbered paragraphs: the first column holds only list markers
        if (c0.All(f => f.Whole && ListMarker.IsMatch(f.Line.Text.Trim()))) return false;
        // signature block: every left cell is a self-contained "Label: value" and no right cell is
        static bool LabelValue(Frag f) { var t = f.Line.Text; int i = t.IndexOf(':'); return i > 0 && i < t.Length - 1; }
        if (c0.All(LabelValue) && !c1.Any(LabelValue) && !c1.All(f => Numeric.IsMatch(f.Line.Text.Trim()))) return false;
        double shortShare0 = c0.Count(ShortOrNumeric) / (double)c0.Count, shortShare1 = c1.Count(ShortOrNumeric) / (double)c1.Count;
        bool short0 = shortShare0 >= 0.7, short1 = shortShare1 >= 0.7;
        if (strongRows < FourRows) return short0 && short1;
        return short0 || short1;
    }

    const int FourRows = 4;

    static List<(float A, float B)> Columns(List<(float A, float B)> gutters, float minX, float maxX)
    {
        var res = new List<(float, float)>();
        float start = minX;
        foreach (var g in gutters) { res.Add((start, g.A)); start = g.B; }
        res.Add((start, maxX));
        return res;
    }

    /// <summary>Column of a piece: the number of gutters that lie wholly left of the piece's left edge (a piece crossing a gutter goes to its first column).</summary>
    static int ColOf(List<(float A, float B)> gutters, Frag f)
    {
        int c = 0;
        foreach (var g in gutters) if (g.B <= f.Box.Left + 1) c++;
        return c;
    }
}
