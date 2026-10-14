using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using SkiaSharp;

namespace IndicOcr.Core;

/// <summary>
/// Writes a PDF whose pages are the original images with an invisible (text render mode 3) Unicode text layer,
/// each line scaled horizontally to its detected box. Text is emitted as Identity-H CID fonts with a ToUnicode map,
/// so selecting, searching and copying return the Unicode text. No shaping is needed because the text is invisible.
/// </summary>
public static class SearchablePdfExporter
{
    /// <summary>Resolution assumed when the page does not say (<see cref="OcrPage.SourceDpi"/> null).</summary>
    public const int DefaultDpi = 300;
    const double MaxPageSidePt = 14400; // PDF/Acrobat limit
    static readonly XStringFormat BaselineLeft = new() { Alignment = XStringAlignment.Near, LineAlignment = XLineAlignment.BaseLine };

    /// <summary>
    /// Writes the pages as a searchable PDF. The page size follows <see cref="OcrPage.SourceDpi"/> (the render dpi of a PDF input, or the
    /// dpi stored in the image) so that a scanned A4 stays A4; it is 300 dpi when unknown. Each image is JPEG-encoded at quality 85.
    /// </summary>
    public static void Write(IEnumerable<(SKBitmap Image, OcrPage Page)> pages, string path, string? fontsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        WriteCore(pages.Select(p => new PageInput(p.Image.Width, p.Image.Height, p.Page, () => EncodeJpeg(p.Image))), path, fontsDirectory);
    }

    /// <summary>
    /// Like the bitmap overload, but takes JPEG bytes that the caller already holds (no re-encode, no quality loss). Each JPEG must be the
    /// image the page's boxes refer to: its pixel size is taken from <see cref="OcrPage.Width"/> and <see cref="OcrPage.Height"/>.
    /// </summary>
    public static void Write(IEnumerable<(byte[] Jpeg, OcrPage Page)> pages, string path, string? fontsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        WriteCore(pages.Select(p => new PageInput(p.Page.Width, p.Page.Height, p.Page, () => p.Jpeg)), path, fontsDirectory);
    }

    readonly record struct PageInput(int Width, int Height, OcrPage Page, Func<byte[]> Jpeg);

    static byte[] EncodeJpeg(SKBitmap image)
    {
        using var skImg = SKImage.FromBitmap(image);
        using var data = skImg.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    /// <summary>Points per pixel for a page: 72 / dpi, shrunk if the page would exceed the 14400 pt PDF limit.</summary>
    internal static double PointsPerPixel(int width, int height, int? dpi)
    {
        double k = 72.0 / (dpi is > 0 ? dpi.Value : DefaultDpi);
        double longSide = Math.Max(width, height) * k;
        return longSide > MaxPageSidePt ? k * MaxPageSidePt / longSide : k;
    }

    static void WriteCore(IEnumerable<PageInput> pages, string path, string? fontsDirectory)
    {
        TextExporter.EnsureDirectory(path);
        PdfFonts.Configure(fontsDirectory);

        using var doc = new PdfDocument();
        int count = 0;
        foreach (var input in pages)
        {
            count++;
            double k = PointsPerPixel(input.Width, input.Height, input.Page.SourceDpi);
            double wPt = input.Width * k, hPt = input.Height * k;
            var pdfPage = doc.AddPage();
            pdfPage.Width = XUnit.FromPoint(wPt);
            pdfPage.Height = XUnit.FromPoint(hPt);

            using (var gfx = XGraphics.FromPdfPage(pdfPage))
            {
                using (var ms = new MemoryStream(input.Jpeg()))
                using (var xi = XImage.FromStream(ms))
                    gfx.DrawImage(xi, 0, 0, wPt, hPt);

                foreach (var line in input.Page.Lines)
                    DrawInvisibleLine(gfx, line, k);
            }
            MakeTextInvisible(pdfPage);
        }
        if (count == 0) doc.AddPage();
        doc.Save(path);
    }

    static void DrawInvisibleLine(XGraphics gfx, OcrLine line, double pxToPt)
    {
        if (string.IsNullOrWhiteSpace(line.Text)) return;
        double x = line.Box.Left * pxToPt, y = line.Box.Top * pxToPt;
        double w = line.Box.Width * pxToPt, h = line.Box.Height * pxToPt;
        if (w <= 0.5 || h <= 0.5) return;
        double size = Math.Max(2, h * 0.8);

        var runs = PdfFonts.SplitRuns(line.Text);
        var fonts = runs.Select(r => PdfFonts.GetFont(r.Script, size)).ToList();
        var widths = new double[runs.Count];
        double total = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            widths[i] = gfx.MeasureString(runs[i].Text, fonts[i], BaselineLeft).Width;
            total += widths[i];
        }
        if (total <= 0.01) return;
        double sx = Math.Clamp(w / total, 0.05, 20);

        var state = gfx.Save();
        gfx.TranslateTransform(x, y + h * 0.82);
        gfx.ScaleTransform(sx, 1);
        double cx = 0;
        for (int i = 0; i < runs.Count; i++)
        {
            gfx.DrawString(runs[i].Text, fonts[i], XBrushes.Black, new XPoint(cx, 0), BaselineLeft);
            cx += widths[i];
        }
        gfx.Restore(state);
    }

    /// <summary>Inserts "3 Tr" (invisible text) after every BT in the page's content streams.</summary>
    static void MakeTextInvisible(PdfPage page)
    {
        foreach (var item in page.Contents.Elements.Items)
        {
            var dict = (item as PdfReference)?.Value as PdfDictionary ?? item as PdfDictionary;
            var stream = dict?.Stream;
            if (stream == null) continue;
            var s = Encoding.Latin1.GetString(stream.UnfilteredValue);
            var patched = PatchBeginText(s);
            if (!ReferenceEquals(patched, s)) stream.Value = Encoding.Latin1.GetBytes(patched);
        }
    }

    internal static string PatchBeginText(string s)
    {
        var sb = new StringBuilder(s.Length + 64);
        bool changed = false;
        int i = 0;
        while (i < s.Length)
        {
            // "BT" as a whole token
            if (s[i] == 'B' && i + 1 < s.Length && s[i + 1] == 'T' &&
                (i == 0 || s[i - 1] == '\n' || s[i - 1] == ' ') &&
                (i + 2 >= s.Length || s[i + 2] == '\n' || s[i + 2] == ' ' || s[i + 2] == '\r'))
            {
                sb.Append("BT 3 Tr");
                i += 2;
                changed = true;
            }
            else sb.Append(s[i++]);
        }
        return changed ? sb.ToString() : s;
    }
}

enum PdfScript { Latin, Telugu, Devanagari, Tamil, Kannada }

/// <summary>Font lookup and script splitting for the PDF text layer.</summary>
sealed class PdfFonts : IFontResolver
{
    static readonly object Gate = new();
    static readonly Dictionary<PdfScript, string> Paths = new();
    static bool _installed;
    static string? _configuredFor;

    static readonly Dictionary<PdfScript, string[]> Candidates = new()
    {
        [PdfScript.Latin] = new[] { "NotoSans-Regular.ttf", "arial.ttf", "segoeui.ttf", "LiberationSans-Regular.ttf", "DejaVuSans.ttf" },
        [PdfScript.Telugu] = new[] { "NotoSansTelugu-Regular.ttf", "NotoSerifTelugu-Regular.ttf", "Nirmala.ttf", "Nirmala.ttc", "gautami.ttf" },
        [PdfScript.Devanagari] = new[] { "NotoSansDevanagari-Regular.ttf", "NotoSerifDevanagari-Regular.ttf", "Nirmala.ttf", "Nirmala.ttc", "mangal.ttf" },
        [PdfScript.Tamil] = new[] { "NotoSansTamil-Regular.ttf", "NotoSerifTamil-Regular.ttf", "Nirmala.ttf", "Nirmala.ttc", "latha.ttf" },
        [PdfScript.Kannada] = new[] { "NotoSansKannada-Regular.ttf", "NotoSerifKannada-Regular.ttf", "Nirmala.ttf", "Nirmala.ttc", "tunga.ttf" },
    };

    public static void Configure(string? fontsDirectory)
    {
        lock (Gate)
        {
            if (!_installed)
            {
                // Core build of PDFsharp has no system font access; all fonts come through this resolver.
                GlobalFontSettings.FontResolver = new PdfFonts();
                _installed = true;
            }
            string key = fontsDirectory ?? "";
            if (_configuredFor == key && Paths.Count > 0) return;
            Paths.Clear();
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(fontsDirectory)) dirs.Add(fontsDirectory);
            dirs.Add(Path.Combine(AppContext.BaseDirectory, "fonts"));
            dirs.Add("/usr/share/fonts/truetype/noto");
            dirs.Add("/usr/share/fonts/truetype/dejavu");
            dirs.Add("/usr/share/fonts/truetype/liberation");
            dirs.Add("/System/Library/Fonts/Supplemental");
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(win)) dirs.Add(win);

            var index = new List<Dictionary<string, string>>();
            foreach (var d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.EnumerateFiles(d))
                {
                    var ext = Path.GetExtension(f);
                    if (ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase))
                        map[Path.GetFileName(f)] = f;
                }
                index.Add(map);
            }
            foreach (var (script, names) in Candidates)
            {
                foreach (var name in names)
                {
                    var hit = index.Select(m => m.TryGetValue(name, out var p) ? p : null).FirstOrDefault(p => p != null && Embeddable(p));
                    if (hit != null) { Paths[script] = hit; break; }
                }
            }
            _configuredFor = key;
        }
    }

    public static XFont GetFont(PdfScript script, double size)
    {
        lock (Gate)
        {
            if (!Paths.ContainsKey(script))
                throw new FileNotFoundException(
                    $"No font with {script} glyphs found for the searchable PDF. Put {Candidates[script][0]} in the fonts folder.");
        }
        return new XFont("IndicOcr-" + script, size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        if (familyName.StartsWith("IndicOcr-", StringComparison.Ordinal))
            return new FontResolverInfo(familyName);
        return new FontResolverInfo("IndicOcr-" + PdfScript.Latin);
    }

    public byte[]? GetFont(string faceName)
    {
        if (!Enum.TryParse<PdfScript>(faceName.Replace("IndicOcr-", ""), out var script)) script = PdfScript.Latin;
        lock (Gate)
        {
            if (!Paths.TryGetValue(script, out var path)) return null;
            return SfntFace(File.ReadAllBytes(path), 0);
        }
    }

    /// <summary>True when the file is TrueType-outline (ttf, otf with glyf, or a ttc): PDFsharp cannot embed CFF (OTTO) fonts.</summary>
    static bool Embeddable(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var h = new byte[4];
            if (fs.Read(h, 0, 4) != 4) return false;
            uint tag = (uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]);
            return tag == 0x00010000 || tag == 0x74727565 /* true */ || tag == 0x74746366 /* ttcf */;
        }
        catch (IOException) { return false; }
    }

    static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    static void Put32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    /// <summary>
    /// Returns a standalone single-face sfnt. A plain font is returned unchanged; for a TrueType collection (.ttc, e.g. Nirmala.ttc)
    /// face <paramref name="index"/> is rebuilt with its own table directory, which is what PDFsharp can embed.
    /// </summary>
    internal static byte[] SfntFace(byte[] data, int index)
    {
        if (data.Length < 16 || U32(data, 0) != 0x74746366) return data;
        uint count = U32(data, 8);
        if (index < 0 || index >= count) index = 0;
        int dir = (int)U32(data, 12 + 4 * index);
        int numTables = data[dir + 4] << 8 | data[dir + 5];
        int headerLen = 12 + 16 * numTables;
        int total = headerLen;
        var lens = new int[numTables];
        for (int i = 0; i < numTables; i++)
        {
            lens[i] = (int)U32(data, dir + 12 + 16 * i + 12);
            total += (lens[i] + 3) & ~3;
        }
        var res = new byte[total];
        Array.Copy(data, dir, res, 0, 12);
        int pos = headerLen;
        for (int i = 0; i < numTables; i++)
        {
            int e = dir + 12 + 16 * i;
            int off = (int)U32(data, e + 8);
            Array.Copy(data, e, res, 12 + 16 * i, 8);
            Put32(res, 12 + 16 * i + 8, (uint)pos);
            Put32(res, 12 + 16 * i + 12, (uint)lens[i]);
            Array.Copy(data, off, res, pos, lens[i]);
            pos += (lens[i] + 3) & ~3;
        }
        return res;
    }

    public static PdfScript ScriptOf(char c) => c switch
    {
        >= 'ऀ' and <= 'ॿ' => PdfScript.Devanagari,
        >= '஀' and <= '௿' => PdfScript.Tamil,
        >= 'ఀ' and <= '౿' => PdfScript.Telugu,
        >= 'ಀ' and <= '೿' => PdfScript.Kannada,
        _ => PdfScript.Latin,
    };

    /// <summary>Splits text into runs that each use one font; joiners stay with the preceding Indic run.</summary>
    internal static List<(string Text, PdfScript Script)> SplitRuns(string text)
    {
        var runs = new List<(string, PdfScript)>();
        var sb = new StringBuilder();
        PdfScript cur = PdfScript.Latin;
        foreach (char c in text)
        {
            var s = ScriptOf(c);
            if ((c == '‌' || c == '‍') && sb.Length > 0) s = cur;
            if (sb.Length > 0 && s != cur)
            {
                runs.Add((sb.ToString(), cur));
                sb.Clear();
            }
            cur = s;
            sb.Append(c);
        }
        if (sb.Length > 0) runs.Add((sb.ToString(), cur));
        return runs;
    }
}
