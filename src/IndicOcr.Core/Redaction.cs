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
    /// <summary>Finds all selected kinds of personal data on the page. Does not modify anything.</summary>
    public static IReadOnlyList<PiiFinding> Find(OcrPage page, RedactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        options ??= new RedactionOptions();
        var result = new List<PiiFinding>();
        var taken = new List<(int Line, int Start, int End)>();
        int group = 0;
        foreach (var vl in PiiRows.Build(page))
        {
            foreach (var m in PiiDetector.Find(vl.Text, options))
            {
                // one segment per source line the match touches
                var segs = new List<(int Line, int Start, int Length)>();
                for (int k = 0; k < vl.Lines.Count; k++)
                {
                    int off = vl.Offsets[k], len = page.Lines[vl.Lines[k]].Text.Length;
                    int a = Math.Max(m.Start, off), b = Math.Min(m.Start + m.Length, off + len);
                    if (b > a) segs.Add((vl.Lines[k], a - off, b - a));
                }
                if (segs.Count == 0) continue;
                // the "keyword row + next row" lines repeat the rows already scanned: only keep what is new
                if (vl.Continuation && segs.Any(sg => taken.Any(t => t.Line == sg.Line && sg.Start < t.End && t.Start < sg.Start + sg.Length))) continue;
                foreach (var sg in segs) taken.Add((sg.Line, sg.Start, sg.Start + sg.Length));
                var masked = PiiGeometry.MaskForDisplay(m.Kind, vl.Text.Substring(m.Start, m.Length));
                foreach (var sg in segs)
                {
                    var line = page.Lines[sg.Line];
                    var quad = PiiGeometry.RegionQuad(line, sg.Start, sg.Length, page.Width, page.Height);
                    result.Add(new PiiFinding(m.Kind, sg.Line, sg.Start, sg.Length, masked, PiiGeometry.Bounds(quad, page.Width, page.Height), quad, m.Verified, group));
                }
                group++;
            }
        }
        return result.OrderBy(f => f.LineIndex).ThenBy(f => f.Start).ToList();
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

    /// <summary>Convenience: <see cref="Find"/> then <see cref="Apply"/> on everything found.</summary>
    public static (SKBitmap Image, OcrPage Page, IReadOnlyList<PiiFinding> Findings) RedactAll(SKBitmap image, OcrPage page, RedactionOptions? options = null)
    {
        options ??= new RedactionOptions();
        var findings = Find(page, options);
        var (img, p) = Apply(image, page, findings, options.MaskChar);
        return (img, p, findings);
    }
}
