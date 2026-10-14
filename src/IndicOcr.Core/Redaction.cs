using System.Drawing;
using System.Text;
using IndicOcr.Core.Internal;
using SkiaSharp;

namespace IndicOcr.Core;

/// <summary>The kind of personal identifier a finding is.</summary>
public enum PiiKind { Aadhaar, Vid, Pan, Phone, Email }

/// <summary>What <see cref="Redactor.Find"/> looks for.</summary>
public sealed record RedactionOptions
{
    /// <summary>
    /// 12-digit Aadhaar number. A number whose Verhoeff checksum passes (first digit 2-9) is always found. A number that fails the
    /// checksum (one misread digit is enough) is still found, but flagged <see cref="PiiFinding.Verified"/> = false, when it is
    /// printed as three groups of four, or when it follows an Aadhaar keyword (Aadhaar, UID, ఆధార్, आधार ...) within three words.
    /// An ungrouped 12-digit run with no keyword must pass the checksum.
    /// </summary>
    public bool Aadhaar { get; init; } = true;
    /// <summary>
    /// PAN: five letters, four digits, one letter, with a valid holder-type letter in position 4. Upper case only, unless a PAN
    /// keyword (PAN, పాన్, पैन, Permanent Account) is within three words before it; then lower case and the usual OCR slips
    /// (0/O, 1/I/l) are tolerated and the finding is flagged unverified.
    /// </summary>
    public bool Pan { get; init; } = true;
    /// <summary>Indian mobile number: 10 digits starting 6-9, optional +91, 91, 0 or 0091 prefix, spaces, hyphens, dashes or dots inside.</summary>
    public bool Phone { get; init; } = true;
    /// <summary>
    /// 16-digit Aadhaar Virtual ID. On by default: a VID can be used instead of the Aadhaar number for e-KYC and authentication, so it
    /// is exactly as sensitive, and a page that shows "VID: 2341 2341 2346 0005" would otherwise leak four digits and a
    /// partly-redacted number is worse than none. Checksum rules as for <see cref="Aadhaar"/>; without a VID or Aadhaar keyword a
    /// 16-digit number must pass the checksum (so card numbers are left alone). When switched off, a VID is left alone as a whole; its first twelve digits are never reported as an Aadhaar number.
    /// </summary>
    public bool Vid { get; init; } = true;
    /// <summary>E-mail addresses. Off by default.</summary>
    public bool Email { get; init; }
    /// <summary>
    /// Character that replaces every matched character in the redacted text. Default 'X': it exists in every font the searchable PDF
    /// embeds (a block like U+2588 is missing from the Noto Sans files and would become a missing-glyph box), it is plain ASCII for
    /// .txt and Word, and it follows the usual "XXXX XXXX 1234" convention. Must not be a digit.
    /// </summary>
    public char MaskChar { get; init; } = 'X';
}

/// <summary>
/// One piece of personal data found on a page. It deliberately holds NO copy of the matched text, only where it is and a masked
/// display string, so a finding is safe to keep in a list, bind to a UI or (by mistake) log.
/// </summary>
/// <param name="Kind">What was found.</param>
/// <param name="LineIndex">Index into <see cref="OcrPage.Lines"/>.</param>
/// <param name="Start">UTF-16 offset of the first matched character in that line's text.</param>
/// <param name="Length">Matched length in UTF-16 units.</param>
/// <param name="MaskedText">For the review list, e.g. "XXXX XXXX 1234" (all but the last four letters or digits hidden; e-mail keeps its first letter and domain).</param>
/// <param name="Box">Axis-aligned bounds of <see cref="Quad"/> in page pixels, clamped to the page.</param>
/// <param name="Quad">The region that will be blacked out: four corners in page pixels (top-left, top-right, bottom-right, bottom-left), following the line's slant.</param>
/// <param name="Verified">True when the match is confirmed by a checksum (Aadhaar, VID) or the strict pattern (PAN, phone, e-mail). False when it was flagged on keyword or layout evidence only (checksum failed, or OCR slips were repaired): the review list should call it "unverified" or "possible".</param>
/// <param name="GroupId">Findings that are parts of one value (a number split over several OCR lines gives one finding per line) share this id. Select or deselect them together.</param>
public sealed record PiiFinding(PiiKind Kind, int LineIndex, int Start, int Length, string MaskedText, RectangleF Box, PointF[] Quad, bool Verified = true, int GroupId = 0);

/// <summary>Finds personal identifiers on an OCR page and removes them from both the image and the text layer.</summary>
/// <remarks>
/// Pixel boxes are APPROXIMATE. The OCR engine reports one box per line, not per character, so the position of a match is
/// estimated from its share of the line (characters weighted by typical glyph width; combining marks count as zero). The estimate can
/// be off by several percent of the line width on lines that mix scripts. To make sure the digits are really covered, the black
/// region is widened on every side (about 0.6 line heights plus 4% of the line length along the line, a quarter line height
/// above and below). It may therefore cover a little text next to the number. Better too much than a visible digit.
/// Lines that share a baseline and sit within about three line heights of each other are read as one line, so a number printed
/// large with wide gaps (the OCR splits it into several lines) is still found; a number is also found when its keyword ends the row
/// above ("Aadhaar No:" / "2341 2341 2346"). A number found over several OCR lines gives one finding (one black quad) per line,
/// all with the same <see cref="PiiFinding.GroupId"/>.
/// Logging: nothing here logs. Findings contain no matched text; keep it that way.
/// LIMITS the app should tell the user about:
/// (1) Numbers written in Indian-language digits (e.g. Telugu ౯౮౪౯౦ ౧౨౩౪౫) are rarely read correctly by the OCR models, so they are
/// often not detected at all (see the measured detection rate in the test suite). Show: "Numbers written in Indian-language
/// digits may not be detected; review the page."
/// (2) Findings with <see cref="PiiFinding.Verified"/> = false are guesses (checksum failed or letters repaired to digits).
/// (3) Only the page given is redacted; the caller must export the redacted page and image, not the original. Redaction is
/// never a guarantee: always look at the result.
/// </remarks>
public static class Redactor
{
    /// <summary>
    /// Finds all selected kinds of personal data on the page. Does not modify anything.
    /// Detection is MONOTONE: it runs on every OCR line alone AND on lines joined into rows, and keeps every candidate from both. The
    /// masked region is the union of all candidate spans, so joining rows can only add coverage. Candidates that overlap (in the same
    /// source line) form one finding group; the best-ranked candidate gives the group's kind and masked display text.
    /// </summary>
    public static IReadOnlyList<PiiFinding> Find(OcrPage page, RedactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        options ??= new RedactionOptions();

        var cands = new List<Cand>();
        void Scan(IReadOnlyList<int> lineIdx, string text, IReadOnlyList<int> offsets)
        {
            foreach (var m in PiiDetector.Find(text, options, offsets))
            {
                // one segment per source line the match touches
                var segs = new List<(int Line, int Start, int Length)>();
                for (int k = 0; k < lineIdx.Count; k++)
                {
                    int off = offsets[k], len = page.Lines[lineIdx[k]].Text.Length;
                    int a = Math.Max(m.Start, off), b = Math.Min(m.Start + m.Length, off + len);
                    if (b > a) segs.Add((lineIdx[k], a - off, b - a));
                }
                if (segs.Count > 0) cands.Add(new Cand(m, segs, PiiGeometry.MaskForDisplay(m.Kind, text.Substring(m.Start, m.Length))));
            }
        }
        for (int i = 0; i < page.Lines.Count; i++)
            if (!string.IsNullOrEmpty(page.Lines[i].Text)) Scan(new[] { i }, page.Lines[i].Text, new[] { 0 });
        foreach (var vl in PiiRows.Build(page))
            if (vl.Lines.Count > 1) Scan(vl.Lines, vl.Text, vl.Offsets);

        // Union-find: candidates whose segments overlap on a line belong together.
        var parent = Enumerable.Range(0, cands.Count).ToArray();
        int Root(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var byLine = new Dictionary<int, List<(int Start, int End, int Cand)>>();
        for (int c = 0; c < cands.Count; c++)
            foreach (var sg in cands[c].Segs)
            {
                if (!byLine.TryGetValue(sg.Line, out var l)) byLine[sg.Line] = l = new();
                l.Add((sg.Start, sg.Start + sg.Length, c));
            }
        foreach (var l in byLine.Values)
        {
            l.Sort((x, y) => x.Start.CompareTo(y.Start));
            int reach = int.MinValue, reachCand = -1;
            foreach (var e in l)
            {
                if (reachCand >= 0 && e.Start < reach) parent[Root(e.Cand)] = Root(reachCand);
                if (e.End > reach) { reach = e.End; reachCand = e.Cand; }
            }
        }

        var comps = cands.Select((c, i) => (c, i)).GroupBy(t => Root(t.i)).Select(g => g.Select(t => t.c).ToList()).ToList();
        var ordered = comps.Select(cs =>
        {
            var spans = cs.SelectMany(c => c.Segs).GroupBy(sg => sg.Line).Select(g => (Line: g.Key, Ranges: MergeRanges(g.Select(sg => (sg.Start, sg.Start + sg.Length))))).OrderBy(x => x.Line).ToList();
            var best = cs[0];
            foreach (var c in cs) if (BetterCand(c, best)) best = c;
            return (Best: best, Spans: spans, First: spans[0].Line, FirstStart: spans[0].Ranges[0].Start);
        }).OrderBy(x => x.First).ThenBy(x => x.FirstStart).ToList();

        var result = new List<PiiFinding>();
        int group = 0;
        foreach (var comp in ordered)
        {
            foreach (var (lineIdx, ranges) in comp.Spans)
                foreach (var (start, end) in ranges)
                {
                    var quad = PiiGeometry.RegionQuad(page.Lines[lineIdx], start, end - start, page.Width, page.Height);
                    result.Add(new PiiFinding(comp.Best.M.Kind, lineIdx, start, end - start, comp.Best.Masked, PiiGeometry.Bounds(quad, page.Width, page.Height), quad, comp.Best.M.Verified, group));
                }
            group++;
        }
        return result.OrderBy(f => f.LineIndex).ThenBy(f => f.Start).ToList();
    }

    /// <summary>Reporting order inside a group: a match that stays in ONE source line first (a window across rows glues neighbours together and mislabels), then <see cref="PiiDetector.Better"/>.</summary>
    static bool BetterCand(Cand a, Cand b)
    {
        bool ao = a.Segs.Count == 1, bo = b.Segs.Count == 1;
        if (ao != bo) return ao;
        return PiiDetector.Better(a.M, b.M);
    }

    sealed record Cand(PiiMatch M, List<(int Line, int Start, int Length)> Segs, string Masked);

    static List<(int Start, int End)> MergeRanges(IEnumerable<(int Start, int End)> ranges)
    {
        var res = new List<(int Start, int End)>();
        foreach (var r in ranges.OrderBy(r => r.Start))
        {
            if (res.Count > 0 && r.Start < res[^1].End) res[^1] = (res[^1].Start, Math.Max(res[^1].End, r.End));
            else res.Add(r);
        }
        return res;
    }

    static List<PiiFinding> Validate(OcrPage page, IEnumerable<PiiFinding> selected, char maskChar)
    {
        if (char.IsDigit(maskChar)) throw new ArgumentException("The mask character must not be a digit.", nameof(maskChar));
        var list = selected.ToList();
        foreach (var f in list)
        {
            if (f.LineIndex < 0 || f.LineIndex >= page.Lines.Count) throw new ArgumentException("A finding does not belong to this page.");
            var t = page.Lines[f.LineIndex].Text;
            if (f.Start < 0 || f.Length <= 0 || f.Start + f.Length > t.Length) throw new ArgumentException("A finding does not fit the page's text.");
        }
        return list;
    }

    /// <summary>
    /// Returns a copy of the page where every matched character of the selected findings is replaced by <paramref name="maskChar"/>
    /// (no image needed). The input is not modified. Use this for text-only exports.
    /// </summary>
    public static OcrPage MaskText(OcrPage page, IEnumerable<PiiFinding> selected, char maskChar = 'X')
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(selected);
        var list = Validate(page, selected, maskChar);
        var lines = page.Lines.ToList();
        foreach (var g in list.GroupBy(f => f.LineIndex))
        {
            var chars = lines[g.Key].Text.ToCharArray();
            foreach (var f in g)
                for (int i = f.Start; i < f.Start + f.Length; i++) chars[i] = maskChar;
            lines[g.Key] = lines[g.Key] with { Text = new string(chars) };
        }
        return page with { Lines = lines };
    }

    /// <summary>
    /// Returns a COPY of the image with the selected regions painted solid black (opaque, overwriting the pixels), and a copy of the
    /// page where every matched character is replaced by <paramref name="maskChar"/>. Inputs are not modified. The caller disposes
    /// the returned bitmap. The bitmap must be the page's own image: a different size (more than 1 pixel off in width or height) throws
    /// <see cref="ArgumentException"/> instead of scaling the boxes, because a mismatch (typically a rotated page) would black out the
    /// wrong place and leave the number visible.
    /// Export the returned pair with the normal exporters: the PDF text layer, .txt and .docx then contain only the mask characters.
    /// </summary>
    public static (SKBitmap Image, OcrPage Page) Apply(SKBitmap image, OcrPage page, IEnumerable<PiiFinding> selected, char maskChar = 'X')
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(selected);
        var list = Validate(page, selected, maskChar);
        if (Math.Abs(image.Width - page.Width) > 1 || Math.Abs(image.Height - page.Height) > 1)
            throw new ArgumentException("The image size does not match the page size (is the page rotated?). Pass the image the page was recognised from.", nameof(image));

        var copy = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(copy))
        {
            canvas.Clear(SKColors.White);
            using (var im = SKImage.FromBitmap(image)) canvas.DrawImage(im, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
            float sx = page.Width > 0 ? (float)image.Width / page.Width : 1f;
            float sy = page.Height > 0 ? (float)image.Height / page.Height : 1f;
            using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false, BlendMode = SKBlendMode.Src };
            foreach (var f in list)
            {
                var pb = new SKPathBuilder();
                pb.MoveTo(f.Quad[0].X * sx, f.Quad[0].Y * sy);
                for (int i = 1; i < f.Quad.Length; i++) pb.LineTo(f.Quad[i].X * sx, f.Quad[i].Y * sy);
                pb.Close();
                using var path = pb.Detach();
                canvas.DrawPath(path, paint);
                // No antialiasing on the fill, so stroke the edge once to leave no half-covered pixel row.
                paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 2;
                canvas.DrawPath(path, paint);
                paint.Style = SKPaintStyle.Fill;
            }
        }
        return (copy, MaskText(page, list, maskChar));
    }

    /// <summary>
    /// Masks personal identifiers in table cell texts (cells that were read again from the image, so no <see cref="PiiFinding"/> exists
    /// for them). Each cell is checked alone and together with the cells of its row, like <see cref="Find"/>; every matched character
    /// is replaced by <paramref name="maskChar"/>. Returns new tables; the input is not modified. A number split over two cells is masked in both.
    /// A number wrapped inside one cell (parts joined by a line feed) is found too; the line feed itself is kept.
    /// <paramref name="keepUnmasked"/> with <paramref name="sourcePage"/>: findings (from <see cref="Find"/> on that page) the user chose NOT to
    /// redact. A value found in a cell is left as it is ONLY if the whole finding group (all its parts, so a number split over two cells
    /// counts as one) has the same kind and the same normalised value (ASCII digits for numbers, upper case letters and digits for PAN,
    /// lower case for e-mail) as such a finding, AND a cell of the group overlaps that finding's source line (unpadded box). The values are
    /// read from <paramref name="sourcePage"/> into a private list that is neither exposed nor logged. A different number in a neighbouring
    /// cell is therefore always masked, whatever the distance. Without <paramref name="sourcePage"/> nothing is kept (everything is masked).
    /// Cell boxes must be in the same pixel space as the page.
    /// A number split by a line feed from its keyword inside one cell ("Rs.\n9849012345") is treated as the page treats it: the value is
    /// matched on its own, so an unticked number stays readable there too.
    /// KNOWN LIMITATION (page level, <see cref="Find"/>): a number wrapped over two vertically STACKED OCR lines with no keyword line
    /// above ("98490" over "12345") is not joined, because rows are built left to right only; each half alone is too short to match.
    /// Inside a table cell this case is covered (see above); on a free-text page it is not. The app's warning to review the pages applies.
    /// </summary>
    public static IReadOnlyList<OcrTable> MaskTables(IReadOnlyList<OcrTable> tables, RedactionOptions? options = null, char maskChar = 'X', OcrPage? sourcePage = null, IEnumerable<PiiFinding>? keepUnmasked = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (char.IsDigit(maskChar)) throw new ArgumentException("The mask character must not be a digit.", nameof(maskChar));
        var keep = BuildKeep(sourcePage, keepUnmasked);
        var result = new List<OcrTable>(tables.Count);
        foreach (var t in tables)
        {
            // one pseudo line per cell, at the cell box, in reading order (so rows join as they would on the page).
            // A wrapped cell has '\n' between its parts; the detector's separators know only spaces, so it sees a space (same length).
            var cells = t.Cells.ToList();
            var lines = cells.Select(c => new OcrLine((c.Text ?? "").Replace('\n', ' ').Replace('\r', ' '), 1f, OcrLanguage.English, c.Box,
                new[] { new PointF(c.Box.Left, c.Box.Top), new PointF(c.Box.Right, c.Box.Top), new PointF(c.Box.Right, c.Box.Bottom), new PointF(c.Box.Left, c.Box.Bottom) })).ToList();
            float w = 1, h = 1;
            foreach (var c in cells) { if (float.IsFinite(c.Box.Right)) w = Math.Max(w, c.Box.Right); if (float.IsFinite(c.Box.Bottom)) h = Math.Max(h, c.Box.Bottom); }
            var page = new OcrPage(0, (int)Math.Min(int.MaxValue / 2, Math.Ceiling(w)), (int)Math.Min(int.MaxValue / 2, Math.Ceiling(h)), lines, TimeSpan.Zero);
            IEnumerable<PiiFinding> found = Find(page, options);
            if (keep.Count > 0) found = DropKept(found, page, cells.Select(c => c.Box).ToList(), keep);
            var masked = MaskText(page, found, maskChar);
            var newCells = new List<OcrTableCell>(cells.Count);
            for (int i = 0; i < cells.Count; i++)
            {
                var orig = cells[i].Text ?? "";
                var m = masked.Lines[i].Text;
                var chars = orig.ToCharArray();
                for (int k = 0; k < chars.Length; k++)
                    if (m[k] != lines[i].Text[k] && chars[k] != '\n' && chars[k] != '\r') chars[k] = m[k]; // a masked character; a line feed inside a masked span stays
                newCells.Add(cells[i] with { Text = new string(chars) });
            }
            result.Add(t with { Cells = newCells });
        }
        return result;
    }

    /// <summary>Normalised value of a finding group, built from the text of its parts. Internal use only; never returned or logged.</summary>
    static string NormValue(PiiKind kind, IEnumerable<string> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
            foreach (var ch in part)
            {
                if (char.IsDigit(ch)) { int d = System.Globalization.CharUnicodeInfo.GetDecimalDigitValue(ch); if (d >= 0) sb.Append((char)('0' + d)); continue; }
                if (kind == PiiKind.Pan) { if (char.IsLetter(ch)) sb.Append(char.ToUpperInvariant(ch)); }
                else if (kind == PiiKind.Email) { if (!char.IsWhiteSpace(ch)) sb.Append(char.ToLowerInvariant(ch)); }
            }
        return sb.ToString();
    }

    static IEnumerable<(PiiKind Kind, string Value, List<PiiFinding> Parts)> Groups(OcrPage page, IEnumerable<PiiFinding> findings)
    {
        foreach (var g in findings.Where(f => f.LineIndex >= 0 && f.LineIndex < page.Lines.Count
                                              && f.Start >= 0 && f.Length > 0 && f.Start + f.Length <= page.Lines[f.LineIndex].Text.Length).GroupBy(f => f.GroupId))
        {
            var parts = g.OrderBy(f => f.LineIndex).ThenBy(f => f.Start).ToList();
            var kind = parts[0].Kind;
            yield return (kind, NormValue(kind, parts.Select(f => page.Lines[f.LineIndex].Text.Substring(f.Start, f.Length))), parts);
        }
    }

    sealed record Keep(PiiKind Kind, string Value, List<RectangleF> LineBoxes)
    {
        // Never print the value: a stray "$"{k}"" in a log or exception must not leak it.
        public override string ToString() => $"Keep({Kind})";
    }

    static List<Keep> BuildKeep(OcrPage? sourcePage, IEnumerable<PiiFinding>? keepUnmasked)
    {
        var res = new List<Keep>();
        if (sourcePage == null || keepUnmasked == null) return res;
        foreach (var (kind, value, parts) in Groups(sourcePage, keepUnmasked))
        {
            if (value.Length == 0) continue;
            res.Add(new Keep(kind, value, parts.Select(f => sourcePage.Lines[f.LineIndex].Box).Where(b => b.Width > 0 && b.Height > 0).ToList()));
        }
        return res;
    }

    static List<PiiFinding> DropKept(IEnumerable<PiiFinding> found, OcrPage page, List<RectangleF> cellBoxes, List<Keep> keep)
    {
        var dropGroups = new HashSet<int>();
        foreach (var (kind, value, parts) in Groups(page, found))
            foreach (var k in keep)
            {
                if (k.Kind != kind || k.Value != value) continue;
                bool near = parts.Any(f => { var cb = cellBoxes[f.LineIndex]; return k.LineBoxes.Any(lb => { var i = RectangleF.Intersect(cb, lb); return i.Width > 0 && i.Height > 0; }); });
                if (near) { dropGroups.Add(parts[0].GroupId); break; }
            }
        return found.Where(f => !dropGroups.Contains(f.GroupId)).ToList();
    }

    /// <summary>Convenience: <see cref="Find"/> then <see cref="Apply"/> on everything found.</summary>
    public static (SKBitmap Image, OcrPage Page, IReadOnlyList<PiiFinding> Findings) RedactAll(SKBitmap image, OcrPage page, RedactionOptions? options = null)
    {
        options ??= new RedactionOptions();
        var findings = Find(page, options);
        var (img, p) = Apply(image, page, findings, options.MaskChar);
        return (img, p, findings);
    }
}
