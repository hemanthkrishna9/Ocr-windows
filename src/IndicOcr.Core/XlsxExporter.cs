using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace IndicOcr.Core;

/// <summary>
/// Writes detected tables to an .xlsx workbook, one sheet per table ("Page N Table M"). Nothing the page read is lost: every page also
/// gets a "Page N text" sheet with one OCR line per row for the lines that are NOT inside a table (a page without tables: all its lines).
/// A page whose lines all sit inside tables gets no text sheet. The workbook is never empty.
/// <para>Cell values: text is stored as shared strings, so Indic Unicode and line breaks are kept exactly. A cell whose whole text is a
/// plain ASCII number (optional sign, digits, optional decimals, optional thousands commas in western 1,234,567 or Indian 12,34,567 grouping)
/// is stored as a number with a number format that reproduces the original look (grouping and number of decimals). Indic digits,
/// currency symbols, percent signs, text, numbers with a leading zero or a leading plus sign, numbers with more than 15 significant digits
/// (a double would round them), Indian-grouped numbers of 10 or more digits (the grouping format stops at 99 crore) and whole numbers of 10 or
/// more digits (phone, Aadhaar, account numbers) stay text. Text is never converted from Indic digits to ASCII.</para>
/// <para>Sheets use the Nirmala UI font, which has Telugu, Devanagari, Tamil and Kannada glyphs on Windows; columns are widened to the content.</para>
/// </summary>
public static class XlsxExporter
{
    const string FontName = "Nirmala UI";
    static readonly Regex NumberRx = new(@"^[+-]?(\d+|\d{1,3}(,\d{3})+|\d{1,2}(,\d{2})*,\d{3})(\.\d+)?$", RegexOptions.CultureInvariant);

    public static void Write(IEnumerable<(OcrPage Page, IReadOnlyList<OcrTable> Tables)> pages, string path)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var styles = new StyleBook();
        var strings = new SharedStrings();
        var sheets = new List<(string Name, SheetModel Model)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (page, tablesIn) in pages)
        {
            int pn = page.PageIndex + 1;
            IReadOnlyList<OcrTable> tables = tablesIn ?? Array.Empty<OcrTable>();
            int m = 0;
            foreach (var t in tables) sheets.Add((Unique(names, $"Page {pn} Table {++m}"), FromTable(t)));
            var outside = page.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text) && !InsideAny(tables, l)).ToList();
            if (outside.Count > 0 || tables.Count == 0)
            {
                var model = new SheetModel();
                int r = 0;
                foreach (var l in outside) model.Cells.Add(new CellModel(r++, 0, 1, 1, l.Text));
                sheets.Add((Unique(names, $"Page {pn} text"), model));
            }
        }
        if (sheets.Count == 0) sheets.Add(("Empty", new SheetModel()));

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new Workbook();
            var sheetsEl = wb.Workbook.AppendChild(new Sheets());
            uint id = 1;
            foreach (var (name, model) in sheets)
            {
                var wsPart = wb.AddNewPart<WorksheetPart>();
                wsPart.Worksheet = BuildSheet(model, styles, strings);
                sheetsEl.Append(new Sheet { Id = wb.GetIdOfPart(wsPart), SheetId = id, Name = name });
                id++;
            }
            var sst = wb.AddNewPart<SharedStringTablePart>();
            sst.SharedStringTable = strings.Build();
            var stylePart = wb.AddNewPart<WorkbookStylesPart>();
            stylePart.Stylesheet = styles.Build();
            wb.Workbook.Save();
        }
    }

    /// <summary>True when the centre of the line lies in a table's bounds (a little inflated: the bounds hug the text).</summary>
    static bool InsideAny(IReadOnlyList<OcrTable> tables, OcrLine l)
    {
        float cx = l.Box.Left + l.Box.Width / 2, cy = l.Box.Top + l.Box.Height / 2;
        foreach (var t in tables)
        {
            var b = System.Drawing.RectangleF.Inflate(t.Bounds, 4, 4);
            if (b.Contains(cx, cy)) return true;
        }
        return false;
    }

    static string Unique(HashSet<string> used, string name)
    {
        string n = name; int k = 2;
        while (!used.Add(n)) n = $"{name} ({k++})";
        return n;
    }

    // ---- model ---------------------------------------------------------------------------------------------------

    sealed record CellModel(int Row, int Col, int RowSpan, int ColSpan, string Text);
    sealed class SheetModel { public List<CellModel> Cells = new(); }

    static SheetModel FromTable(OcrTable t)
    {
        var m = new SheetModel();
        foreach (var c in t.Cells)
            if (c.Text.Length > 0 || c.RowSpan > 1 || c.ColSpan > 1) m.Cells.Add(new CellModel(c.Row, c.Column, c.RowSpan, c.ColSpan, c.Text));
        return m;
    }

    // ---- sheet ---------------------------------------------------------------------------------------------------

    static Worksheet BuildSheet(SheetModel model, StyleBook styles, SharedStrings strings)
    {
        var ws = new Worksheet();
        var data = new SheetData();
        var widths = new Dictionary<int, double>();
        var merges = new List<string>();

        foreach (var rowGroup in model.Cells.GroupBy(c => c.Row).OrderBy(g => g.Key))
        {
            var row = new Row { RowIndex = (uint)(rowGroup.Key + 1) };
            foreach (var c in rowGroup.OrderBy(c => c.Col))
            {
                string text = Clean(c.Text);
                var cell = new Cell { CellReference = Ref(c.Col, c.Row) };
                if (TryNumber(text, out double value, out string fmt))
                {
                    cell.StyleIndex = styles.Index(fmt);
                    cell.CellValue = new CellValue(value.ToString("R", CultureInfo.InvariantCulture));
                }
                else
                {
                    cell.StyleIndex = styles.Index(null);
                    cell.DataType = CellValues.SharedString;
                    cell.CellValue = new CellValue(strings.Add(text).ToString(CultureInfo.InvariantCulture));
                }
                row.Append(cell);
                if (c.RowSpan > 1 || c.ColSpan > 1)
                {
                    merges.Add($"{Ref(c.Col, c.Row)}:{Ref(c.Col + c.ColSpan - 1, c.Row + c.RowSpan - 1)}");
                    // pad merged-over cells with the same border so the merged block is drawn whole
                }
                else
                {
                    widths[c.Col] = Math.Max(widths.GetValueOrDefault(c.Col), Width(text));
                }
            }
            data.Append(row);
        }

        var cols = new Columns();
        foreach (var kv in widths.OrderBy(k => k.Key))
            cols.Append(new Column { Min = (uint)(kv.Key + 1), Max = (uint)(kv.Key + 1), Width = Math.Clamp(kv.Value, 8, 70), CustomWidth = true });
        if (cols.HasChildren) ws.Append(cols);
        ws.Append(data);
        if (merges.Count > 0)
        {
            var mc = new MergeCells();
            foreach (var m in merges) mc.Append(new MergeCell { Reference = m });
            ws.Append(mc);
        }
        return ws;
    }

    /// <summary>Column width in Excel characters: the longest line of the cell. Indic glyphs are wider than a digit, so they count 1.25.</summary>
    static double Width(string text)
    {
        double best = 0;
        foreach (var line in text.Split('\n'))
        {
            double w = 0;
            foreach (var ch in line) w += ch >= 0x0900 ? 1.25 : 1.0;
            best = Math.Max(best, w);
        }
        return best + 2;
    }

    static string Ref(int col, int row)
    {
        var sb = new StringBuilder();
        for (int c = col + 1; c > 0; c = (c - 1) / 26) sb.Insert(0, (char)('A' + (c - 1) % 26));
        return sb.Append(row + 1).ToString();
    }

    /// <summary>Drops characters XML cannot carry (control characters other than tab, line feed, carriage return) and normalises CRLF.</summary>
    static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '\r') continue;
            if (ch < 0x20 && ch != '\t' && ch != '\n') continue;
            if (ch == 0xFFFE || ch == 0xFFFF) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    static bool TryNumber(string text, out double value, out string format)
    {
        value = 0; format = "";
        var s = text.Trim();
        if (s.Length == 0 || s.Length > 30 || s != text || s[0] == '+' || !NumberRx.IsMatch(s)) return false; // "+5" stays text (the sign would be lost)
        bool neg = s[0] == '-';
        string body = s.TrimStart('+', '-');
        string intPart = body.Split('.')[0];
        string digits = intPart.Replace(",", "");
        string dec = body.Contains('.') ? body[(body.IndexOf('.') + 1)..] : "";
        bool grouped = intPart.Contains(',');
        bool indian = grouped && intPart.Split(',').Length > 2 && intPart.Split(',')[1].Length == 2;
        if (digits.Length > 1 && digits[0] == '0') return false;       // 007, 0123: an identifier
        if (dec.Length == 0 && digits.Length >= 10) return false;      // phone, Aadhaar, account number
        if ((digits + dec).TrimStart('0').Length > 15) return false;    // double keeps 15 significant digits (integer and decimals together)
        if (indian && digits.Length >= 10) return false;                // the lakh/crore format below groups correctly up to 9 digits (99 crore)
        if (indian && neg) return false;                                // the lakh format below has no sign handling
        if (!double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)) return false;
        string d = dec.Length > 0 ? "." + new string('0', dec.Length) : "";
        if (indian) format = $"[>=10000000]##\\,##\\,##\\,##0{d};[>=100000]##\\,##\\,##0{d};##,##0{d}";
        else if (grouped) format = "#,##0" + d;
        else format = dec.Length > 0 ? "0" + d : "0";
        return true;
    }

    // ---- shared strings and styles -----------------------------------------------------------------------------------

    sealed class SharedStrings
    {
        readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        readonly List<string> _items = new();
        public int Add(string s)
        {
            if (_index.TryGetValue(s, out int i)) return i;
            _index[s] = _items.Count; _items.Add(s);
            return _items.Count - 1;
        }
        public SharedStringTable Build()
        {
            var t = new SharedStringTable { Count = (uint)_items.Count, UniqueCount = (uint)_items.Count };
            foreach (var s in _items)
                t.Append(new SharedStringItem(new Text(s) { Space = SpaceProcessingModeValues.Preserve }));
            return t;
        }
    }

    /// <summary>Cell formats: one text style (wrap, top aligned, thin border, Nirmala UI) and one per number format.</summary>
    sealed class StyleBook
    {
        readonly List<string?> _formats = new() { null };
        public uint Index(string? numberFormat)
        {
            int i = _formats.IndexOf(numberFormat);
            if (i < 0) { _formats.Add(numberFormat); i = _formats.Count - 1; }
            return (uint)i;
        }

        public Stylesheet Build()
        {
            var ss = new Stylesheet();
            var custom = _formats.Where(f => f != null && f != "0").Distinct().ToList();
            uint FmtId(string? f) => f == null ? 0u : f == "0" ? 1u : 164u + (uint)custom.IndexOf(f);
            if (custom.Count > 0)
            {
                var nf = new NumberingFormats();
                foreach (var f in custom) nf.Append(new NumberingFormat { NumberFormatId = FmtId(f), FormatCode = f! });
                ss.Append(nf);
            }
            ss.Append(new Fonts(new Font(new FontSize { Val = 11 }, new FontName { Val = FontName }, new FontFamilyNumbering { Val = 2 })) { Count = 1 });
            ss.Append(new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 });
            var thin = (BorderStyleValues.Thin);
            ss.Append(new Borders(
                new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder()),
                new Border(
                    new LeftBorder(new Color { Auto = true }) { Style = thin },
                    new RightBorder(new Color { Auto = true }) { Style = thin },
                    new TopBorder(new Color { Auto = true }) { Style = thin },
                    new BottomBorder(new Color { Auto = true }) { Style = thin },
                    new DiagonalBorder())) { Count = 2 });
            ss.Append(new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 });
            var xfs = new CellFormats();
            foreach (var f in _formats)
                xfs.Append(new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Top, WrapText = f == null })
                {
                    NumberFormatId = FmtId(f), FontId = 0, FillId = 0, BorderId = 1, FormatId = 0,
                    ApplyNumberFormat = f != null, ApplyFont = true, ApplyBorder = true, ApplyAlignment = true,
                });
            xfs.Count = (uint)_formats.Count;
            ss.Append(xfs);
            ss.Append(new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 });
            return ss;
        }
    }
}
