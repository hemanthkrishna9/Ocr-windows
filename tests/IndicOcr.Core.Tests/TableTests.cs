using System.Diagnostics;
using System.Drawing;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using IndicOcr.Core.Internal;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Xunit;
using Xunit.Abstractions;

namespace IndicOcr.Core.Tests;

/// <summary>Table detection and the Excel export. The synthetic tests draw rectangles and bars (no fonts, no models); the model tests render real text.</summary>
[Collection("Timing")]
public class TableTests
{
    readonly ITestOutputHelper _out;
    public TableTests(ITestOutputHelper o) => _out = o;

    // ---- synthetic drawing helpers ---------------------------------------------------------------------------------

    static OcrLine Line(string text, float x, float y, float w, float h, OcrLanguage lang = OcrLanguage.English) =>
        new(text, 0.95f, lang, new RectangleF(x, y, w, h), new[] { new PointF(x, y), new PointF(x + w, y), new PointF(x + w, y + h), new PointF(x, y + h) });

    static OcrPage Page(SKBitmap bmp, params OcrLine[] lines) => new(0, bmp.Width, bmp.Height, lines, TimeSpan.Zero);

    static SKBitmap Canvas(int w, int h, out SKCanvas c)
    {
        var bmp = new SKBitmap(w, h);
        c = new SKCanvas(bmp);
        c.Clear(SKColors.White);
        return bmp;
    }

    static readonly SKPaint Black = new() { Color = SKColors.Black, IsAntialias = false };

    static void Bar(SKCanvas c, float x, float y, float w, float h = 30) => c.DrawRect(x, y, w, h, Black);

    static void HLine(SKCanvas c, float y, float x0, float x1, float t = 3) => c.DrawRect(x0, y - t / 2, x1 - x0, t, Black);
    static void VLine(SKCanvas c, float x, float y0, float y1, float t = 3) => c.DrawRect(x - t / 2, y0, t, y1 - y0, Black);

    static readonly float[] Xs = { 100, 400, 700, 1000 };
    static float[] Ys(int rows) => Enumerable.Range(0, rows + 1).Select(i => 100f + 70 * i).ToArray();

    /// <summary>A bordered grid with a text bar and one OCR line per cell. skipV(row, boundaryIndex) leaves a vertical wall out.</summary>
    static (SKBitmap Bmp, OcrPage Page) BorderedGrid(int rows, Func<int, int, bool>? skipV = null, Func<int, int, string>? text = null, bool oneLinePerCell = true)
    {
        var ys = Ys(rows);
        var bmp = Canvas(1200, (int)ys[^1] + 100, out var c);
        using (c)
        {
            foreach (var y in ys) HLine(c, y, Xs[0], Xs[^1]);
            for (int b = 0; b < Xs.Length; b++)
                for (int r = 0; r < rows; r++)
                    if (skipV == null || b == 0 || b == Xs.Length - 1 || !skipV(r, b)) VLine(c, Xs[b], ys[r], ys[r + 1] + 1.5f);
            var lines = new List<OcrLine>();
            for (int r = 0; r < rows; r++)
                for (int col = 0; col < 3; col++)
                {
                    if (skipV != null && col > 0 && skipV(r, col)) continue; // text only in the first cell of a merged block
                    string t = text?.Invoke(r, col) ?? $"r{r}c{col}";
                    float w = 150;
                    if (skipV != null && col == 0 && skipV(r, 1)) w = 600;
                    Bar(c, Xs[col] + 20, ys[r] + 20, w);
                    lines.Add(Line(t, Xs[col] + 14, ys[r] + 14, w + 12, 42));
                }
            c.Flush();
            return (bmp, Page(bmp, lines.ToArray()));
        }
    }

    // ---- ruled tables ----------------------------------------------------------------------------------------------

    [Fact]
    public void Bordered_grid_is_detected_with_cell_texts_and_no_reread()
    {
        var (bmp, page) = BorderedGrid(4);
        using var _ = bmp;
        int reads = 0;
        var tables = TableBuilder.Build(bmp, page, _ => { reads++; return "?"; });
        var t = Assert.Single(tables);
        Assert.True(t.HasRulings);
        Assert.Equal((4, 3), (t.Rows, t.Columns));
        Assert.Equal(12, t.Cells.Count);
        Assert.All(t.Cells, c => Assert.Equal($"r{c.Row}c{c.Column}", c.Text));
        Assert.All(t.Cells, c => Assert.Equal((1, 1), (c.RowSpan, c.ColSpan)));
        Assert.Equal(0, reads);
        Assert.InRange(t.Bounds.Left, 95, 105);
        Assert.InRange(t.Bounds.Right, 995, 1005);
    }

    [Fact]
    public void Merged_header_cell_spans_three_columns_when_borders_are_missing()
    {
        var (bmp, page) = BorderedGrid(4, skipV: (r, b) => r == 0, text: (r, c) => r == 0 ? "Header" : $"r{r}c{c}");
        using var _ = bmp;
        var t = Assert.Single(TableBuilder.Build(bmp, page, null));
        Assert.Equal((4, 3), (t.Rows, t.Columns));
        var head = t.Cells.Single(c => c.Row == 0);
        Assert.Equal((0, 3, 1), (head.Column, head.ColSpan, head.RowSpan));
        Assert.Equal("Header", head.Text);
        Assert.Equal(1 + 9, t.Cells.Count);
        var g = t.ToGrid();
        Assert.Equal("Header", g[0, 0]);
        Assert.Equal("", g[0, 1]);
        Assert.Equal("r3c2", g[3, 2]);
    }

    [Fact]
    public void A_line_crossing_a_border_is_split_and_each_part_is_read_again()
    {
        // one detector box covers the texts of all three cells of each row
        var ys = Ys(3);
        using var bmp = Canvas(1200, 500, out var c);
        foreach (var y in ys) HLine(c, y, Xs[0], Xs[^1]);
        foreach (var x in Xs) VLine(c, x, ys[0], ys[^1] + 1.5f);
        var lines = new List<OcrLine>();
        for (int r = 0; r < 3; r++)
        {
            for (int col = 0; col < 3; col++) Bar(c, Xs[col] + 20, ys[r] + 20, 150);
            lines.Add(Line("merged blob", Xs[0] + 14, ys[r] + 14, 2 * 300 + 150 + 12, 42));
        }
        c.Flush(); c.Dispose();
        var page = Page(bmp, lines.ToArray());

        var read = new List<RectangleF>();
        string Reader(RectangleF b)
        {
            read.Add(b);
            int row = (int)((b.Top + b.Height / 2 - 100) / 70);
            int col = b.Left + b.Width / 2 < 400 ? 0 : b.Left + b.Width / 2 < 700 ? 1 : 2;
            return $"r{row}c{col}";
        }
        var t = Assert.Single(TableBuilder.Build(bmp, page, Reader));
        Assert.Equal(9, read.Count);
        Assert.All(t.Cells, cell => Assert.Equal($"r{cell.Row}c{cell.Column}", cell.Text));
        // each read stays inside its own cell
        Assert.All(read, b => Assert.True(Xs.Count(x => x > b.Left + 1 && x < b.Right - 1) == 0, "crop crosses a border"));

        // without an engine the whole text lands in one cell instead of being duplicated
        var plain = Assert.Single(TableExtractor.Extract(bmp, page));
        Assert.Equal(3, plain.Cells.Count(cell => cell.Text == "merged blob"));
    }

    [Fact]
    public void A_framed_paragraph_is_not_a_table()
    {
        using var bmp = Canvas(1200, 600, out var c);
        HLine(c, 80, 80, 1100); HLine(c, 520, 80, 1100); VLine(c, 80, 80, 521.5f); VLine(c, 1100, 80, 521.5f);
        var lines = new List<OcrLine>();
        for (int i = 0; i < 8; i++) { Bar(c, 120, 120 + 50 * i, 940); lines.Add(Line($"line {i}", 114, 114 + 50 * i, 952, 42)); }
        c.Flush(); c.Dispose();
        Assert.Empty(TableBuilder.Build(bmp, Page(bmp, lines.ToArray()), null));
    }

    [Fact]
    public void Bitmap_that_does_not_match_the_page_is_rejected()
    {
        using var bmp = Canvas(500, 500, out var c); c.Dispose();
        var page = new OcrPage(0, 700, 500, new[] { Line("x", 10, 10, 100, 30) }, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => TableExtractor.Extract(bmp, page));
    }

    // ---- aligned (borderless) tables --------------------------------------------------------------------------------

    [Fact]
    public void Borderless_table_with_one_box_per_cell_is_found()
    {
        using var bmp = Canvas(1200, 600, out var c);
        var lines = new List<OcrLine>();
        float[] x = { 100, 400, 700 };
        for (int r = 0; r < 6; r++)
            for (int col = 0; col < 3; col++)
            {
                float w = 100 + 30 * ((r + col) % 4);
                Bar(c, x[col], 100 + 70 * r, w);
                lines.Add(Line($"r{r}c{col}", x[col] - 6, 94 + 70 * r, w + 12, 42));
            }
        c.Flush(); c.Dispose();
        var t = Assert.Single(TableBuilder.Build(bmp, Page(bmp, lines.ToArray()), null));
        Assert.False(t.HasRulings);
        Assert.Equal((6, 3), (t.Rows, t.Columns));
        Assert.All(t.Cells, cell => Assert.Equal($"r{cell.Row}c{cell.Column}", cell.Text));
    }

    [Fact]
    public void Borderless_table_where_each_row_is_one_wide_box_is_split_at_the_gaps()
    {
        using var bmp = Canvas(1200, 600, out var c);
        var lines = new List<OcrLine>();
        float[] x = { 100, 400, 700 };
        for (int r = 0; r < 5; r++)
        {
            for (int col = 0; col < 3; col++) Bar(c, x[col], 100 + 70 * r, 150);
            lines.Add(Line("row blob", 94, 94 + 70 * r, 762, 42));
        }
        c.Flush(); c.Dispose();
        int reads = 0;
        var t = Assert.Single(TableBuilder.Build(bmp, Page(bmp, lines.ToArray()), b =>
        {
            reads++;
            int row = (int)((b.Top + b.Height / 2 - 94) / 70);
            return $"r{row}c{(b.Left < 300 ? 0 : b.Left < 600 ? 1 : 2)}";
        }));
        Assert.Equal(15, reads);
        Assert.Equal((5, 3), (t.Rows, t.Columns));
        Assert.All(t.Cells, cell => Assert.Equal($"r{cell.Row}c{cell.Column}", cell.Text));
    }

    [Fact]
    public void Label_value_form_is_a_two_column_table()
    {
        using var bmp = Canvas(1400, 500, out var c);
        var lines = new List<OcrLine>();
        for (int r = 0; r < 5; r++)
        {
            Bar(c, 100, 100 + 70 * r, 160); lines.Add(Line($"label{r}", 94, 94 + 70 * r, 172, 42));
            Bar(c, 700, 100 + 70 * r, 100 + 40 * (r % 3)); lines.Add(Line($"value{r}", 694, 94 + 70 * r, 112 + 40 * (r % 3), 42));
        }
        c.Flush(); c.Dispose();
        var t = Assert.Single(TableBuilder.Build(bmp, Page(bmp, lines.ToArray()), null));
        Assert.Equal((5, 2), (t.Rows, t.Columns));
        Assert.Equal("label3", t.ToGrid()[3, 0]);
        Assert.Equal("value3", t.ToGrid()[3, 1]);
    }

    [Fact]
    public void Plain_paragraph_and_two_column_prose_are_not_tables()
    {
        using var bmp = Canvas(1600, 900, out var c);
        var lines = new List<OcrLine>();
        for (int i = 0; i < 12; i++) { Bar(c, 100, 60 + 50 * i, 1400); lines.Add(Line("p", 94, 54 + 50 * i, 1412, 42)); }
        c.Flush(); c.Dispose();
        Assert.Empty(TableBuilder.Build(bmp, Page(bmp, lines.ToArray()), null));

        using var bmp2 = Canvas(1600, 900, out var c2);
        var lines2 = new List<OcrLine>();
        for (int i = 0; i < 12; i++)
            foreach (float x in new[] { 100f, 850f }) { Bar(c2, x, 60 + 50 * i, 650); lines2.Add(Line("p", x - 6, 54 + 50 * i, 662, 42)); }
        c2.Flush(); c2.Dispose();
        Assert.Empty(TableBuilder.Build(bmp2, Page(bmp2, lines2.ToArray()), null));
    }

    [Fact]
    public void Two_short_rows_are_not_enough_for_a_borderless_table()
    {
        using var bmp = Canvas(1200, 300, out var c);
        var lines = new List<OcrLine>();
        for (int r = 0; r < 2; r++)
            foreach (float x in new[] { 100f, 500f }) { Bar(c, x, 100 + 70 * r, 150); lines.Add(Line("t", x - 6, 94 + 70 * r, 162, 42)); }
        c.Flush(); c.Dispose();
        Assert.Empty(TableBuilder.Build(bmp, Page(bmp, lines.ToArray()), null));
    }

    // ---- false positives (layouts that look like columns but are not tables) -------------------------------------------

    /// <summary>Rows of (x, width) bars with one OCR line per bar; texts[row][bar].</summary>
    static (SKBitmap Bmp, OcrPage Page) Rows(int h, (float X, float W)[][] bars, string[][] texts)
    {
        var bmp = Canvas(1600, h, out var c);
        var lines = new List<OcrLine>();
        for (int r = 0; r < bars.Length; r++)
            for (int k = 0; k < bars[r].Length; k++)
            {
                var (x, w) = bars[r][k];
                Bar(c, x, 100 + 70 * r, w);
                lines.Add(Line(texts[r][k], x - 6, 94 + 70 * r, w + 12, 42));
            }
        c.Flush(); c.Dispose();
        return (bmp, Page(bmp, lines.ToArray()));
    }

    [Fact]
    public void Numbered_paragraphs_are_not_a_table_but_a_serial_number_column_in_a_wider_table_is()
    {
        // "1." / "(a)" / Telugu digit markers on the left, wrapped paragraph text on the right
        foreach (var markers in new[] { new[] { "1.", "2.", "3.", "4." }, new[] { "(a)", "(b)", "(c)", "(d)" }, new[] { "\u0C67.", "\u0C68.", "\u0C69.", "\u0C6A." }, new[] { "i)", "ii)", "iii)", "iv)" } })
        {
            var bars = Enumerable.Range(0, 4).Select(_ => new[] { (150f, 50f), (300f, 900f) }).ToArray();
            var (bmp, page) = Rows(500, bars, markers.Select(m => new[] { m, "The petitioner is the absolute owner of the land" }).ToArray());
            using var _ = bmp;
            Assert.Empty(TableBuilder.Build(bmp, page, null));
        }
        // the same markers as the first column of THREE columns are a numbered table
        var bars3 = Enumerable.Range(0, 4).Select(_ => new[] { (150f, 50f), (400f, 200f), (800f, 150f) }).ToArray();
        var (b3, p3) = Rows(500, bars3, Enumerable.Range(1, 4).Select(i => new[] { $"{i}.", "Ravi Kumar", "1500" }).ToArray());
        using var __ = b3;
        var t = Assert.Single(TableBuilder.Build(b3, p3, null));
        Assert.Equal((4, 3), (t.Rows, t.Columns));
    }

    [Fact]
    public void Letter_signature_block_is_not_a_table()
    {
        var bars = Enumerable.Range(0, 4).Select(_ => new[] { (150f, 250f), (900f, 250f) }).ToArray();
        var (bmp, page) = Rows(500, bars, new[]
        {
            new[] { "Place: Hyderabad", "Yours faithfully," }, new[] { "Date: 12-05-2024", "(K. Rama Rao)" },
            new[] { "Encl: Aadhaar copy", "Advocate" }, new[] { "Ph: 040-2345678", "Enrol No. AP/123/2001" },
        });
        using var _ = bmp;
        Assert.Empty(TableBuilder.Build(bmp, page, null));
        // a form of Label: value fields on both sides is still a table
        var (b2, p2) = Rows(500, bars, Enumerable.Range(0, 4).Select(i => new[] { $"Name: Ravi{i}", $"Age: 4{i}" }).ToArray());
        using var __ = b2;
        Assert.Single(TableBuilder.Build(b2, p2, null));
    }

    [Fact]
    public void Two_columns_of_long_text_and_short_three_row_pairs_are_not_tables()
    {
        // 4 rows, both columns long (380 px = 9 line heights): a two-column list, not a table
        var longBars = Enumerable.Range(0, 4).Select(_ => new[] { (100f, 380f), (800f, 380f) }).ToArray();
        var (bmp, page) = Rows(500, longBars, Enumerable.Range(0, 4).Select(i => new[] { $"a long phrase {i}", $"another long phrase {i}" }).ToArray());
        using var _ = bmp;
        Assert.Empty(TableBuilder.Build(bmp, page, null));

        // 3 rows, one short and one long column: not enough evidence
        var mixed = Enumerable.Range(0, 3).Select(_ => new[] { (100f, 150f), (800f, 380f) }).ToArray();
        var (b2, p2) = Rows(400, mixed, Enumerable.Range(0, 3).Select(i => new[] { $"label{i}", $"a long phrase {i}" }).ToArray());
        using var __ = b2;
        Assert.Empty(TableBuilder.Build(b2, p2, null));

        // 3 rows of short pairs, and 4 rows of label + long value, are tables
        var shortPairs = Enumerable.Range(0, 3).Select(_ => new[] { (100f, 150f), (800f, 150f) }).ToArray();
        var (b3, p3) = Rows(400, shortPairs, Enumerable.Range(0, 3).Select(i => new[] { $"k{i}", $"v{i}" }).ToArray());
        using var ___ = b3;
        Assert.Single(TableBuilder.Build(b3, p3, null));
        var labelLong = Enumerable.Range(0, 4).Select(_ => new[] { (100f, 150f), (800f, 380f) }).ToArray();
        var (b4, p4) = Rows(500, labelLong, Enumerable.Range(0, 4).Select(i => new[] { $"label{i}", $"a long phrase {i}" }).ToArray());
        using var ____ = b4;
        Assert.Single(TableBuilder.Build(b4, p4, null));
    }

    [Fact]
    public void Frame_with_a_rule_under_the_title_is_not_a_one_column_table_but_a_list_of_short_cells_is()
    {
        using var bmp = Canvas(1200, 700, out var c);
        HLine(c, 80, 80, 1100); HLine(c, 200, 80, 1100); HLine(c, 620, 80, 1100); VLine(c, 80, 80, 621.5f); VLine(c, 1100, 80, 621.5f);
        Bar(c, 450, 120, 300); Bar(c, 120, 240, 940);
        c.Flush(); c.Dispose();
        var page = Page(bmp, Line("CERTIFICATE", 444, 114, 312, 42),
            Line("This is to certify that the property described below stands in the name of the applicant", 114, 234, 952, 42));
        Assert.Empty(TableBuilder.Build(bmp, page, null));

        using var bmp2 = Canvas(1200, 700, out var c2);
        HLine(c2, 80, 80, 600); HLine(c2, 150, 80, 600); HLine(c2, 220, 80, 600); VLine(c2, 80, 80, 221.5f); VLine(c2, 600, 80, 221.5f);
        Bar(c2, 120, 100, 200); Bar(c2, 120, 170, 200);
        c2.Flush(); c2.Dispose();
        var t = Assert.Single(TableBuilder.Build(bmp2, Page(bmp2, Line("Item", 114, 94, 212, 42), Line("Rice", 114, 164, 212, 42)), null));
        Assert.Equal((2, 1), (t.Rows, t.Columns));
    }

    [Fact]
    public void Auto_cells_are_read_with_the_pages_detected_languages_not_a_new_vote()
    {
        // TableExtractor.Extract resolves Auto to the page's DetectedLanguages, so the per-cell reads need no script vote.
        var page = new OcrPage(0, 10, 10, Array.Empty<OcrLine>(), TimeSpan.Zero) { DetectedLanguages = new[] { OcrLanguage.Hindi, OcrLanguage.English } };
        Assert.Equal(new[] { OcrLanguage.Hindi, OcrLanguage.English }, TableExtractor.ResolveCellLanguages(new[] { OcrLanguage.Auto }, page));
        Assert.Equal(new[] { OcrLanguage.Telugu }, TableExtractor.ResolveCellLanguages(new[] { OcrLanguage.Telugu }, page));
        var none = new OcrPage(0, 10, 10, Array.Empty<OcrLine>(), TimeSpan.Zero);
        Assert.Equal(new[] { OcrLanguage.Auto }, TableExtractor.ResolveCellLanguages(new[] { OcrLanguage.Auto }, none));
    }

    // ---- xlsx --------------------------------------------------------------------------------------------------------

    static string Tmp(string ext) => Path.Combine(Path.GetTempPath(), "tables_" + Guid.NewGuid().ToString("N") + ext);

    sealed record XCell(string Text, bool IsNumber, string? Format);

    static Dictionary<string, Dictionary<string, XCell>> ReadWorkbook(string path, out Dictionary<string, List<string>> merges, out string fontName)
    {
        var res = new Dictionary<string, Dictionary<string, XCell>>();
        merges = new();
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        var sst = wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToList() ?? new();
        var styles = wb.WorkbookStylesPart!.Stylesheet!;
        fontName = styles.Fonts!.Elements<Font>().First().GetFirstChild<FontName>()!.Val!.Value!;
        var xfs = styles.CellFormats!.Elements<CellFormat>().ToList();
        var fmts = styles.NumberingFormats?.Elements<NumberingFormat>().ToDictionary(n => n.NumberFormatId!.Value, n => n.FormatCode!.Value!) ?? new Dictionary<uint, string>();
        foreach (var sh in wb.Workbook!.Sheets!.Elements<Sheet>())
        {
            string name = sh.Name!.Value!;
            var ws = ((WorksheetPart)wb.GetPartById(sh.Id!)).Worksheet!;
            var d = new Dictionary<string, XCell>();
            foreach (var cell in ws.Descendants<Cell>())
            {
                string v = cell.CellValue?.Text ?? "";
                if (cell.DataType?.Value == CellValues.SharedString) d[cell.CellReference!.Value!] = new XCell(sst[int.Parse(v)], false, null);
                else
                {
                    uint id = xfs[(int)(cell.StyleIndex?.Value ?? 0)].NumberFormatId!.Value;
                    d[cell.CellReference!.Value!] = new XCell(v, true, fmts.TryGetValue(id, out var code) ? code : id == 1 ? "0" : null);
                }
            }
            res[name] = d;
            merges[name] = ws.Descendants<MergeCell>().Select(m => m.Reference!.Value!).ToList();
        }
        return res;
    }

    static OcrTable Tbl(int rows, int cols, params string[] texts)
    {
        var cells = new List<OcrTableCell>();
        for (int i = 0; i < texts.Length; i++) cells.Add(new OcrTableCell(i / cols, i % cols, 1, 1, texts[i], new RectangleF(0, 0, 10, 10)));
        return new OcrTable(new RectangleF(0, 0, 10, 10), rows, cols, cells);
    }

    [Fact]
    public void Xlsx_has_one_sheet_per_table_and_text_sheet_for_pages_without_tables()
    {
        var p1 = new OcrPage(0, 100, 100, new[] { Line("x", 0, 0, 1, 1) }, TimeSpan.Zero);
        var p2 = new OcrPage(1, 100, 100, new[] { Line("first line", 0, 0, 1, 1), Line("రెండవ పంక్తి", 0, 5, 1, 1) }, TimeSpan.Zero);
        var t1 = Tbl(2, 2, "పేరు", "వయస్సు", "రాజేష్", "42");
        var t2 = Tbl(1, 1, "only");
        var path = Tmp(".xlsx");
        try
        {
            XlsxExporter.Write(new (OcrPage, IReadOnlyList<OcrTable>)[] { (p1, new[] { t1, t2 }), (p2, Array.Empty<OcrTable>()) }, path);
            var wb = ReadWorkbook(path, out _, out var font);
            Assert.Equal(new[] { "Page 1 Table 1", "Page 1 Table 2", "Page 2 text" }, wb.Keys.ToArray());
            Assert.Equal("Nirmala UI", font);
            Assert.Equal("పేరు", wb["Page 1 Table 1"]["A1"].Text);
            Assert.Equal("వయస్సు", wb["Page 1 Table 1"]["B1"].Text);
            Assert.Equal("రాజేష్", wb["Page 1 Table 1"]["A2"].Text);
            Assert.True(wb["Page 1 Table 1"]["B2"].IsNumber);
            Assert.Equal("42", wb["Page 1 Table 1"]["B2"].Text);
            Assert.Equal("first line", wb["Page 2 text"]["A1"].Text);
            Assert.Equal("రెండవ పంక్తి", wb["Page 2 text"]["A2"].Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Xlsx_numbers_are_numeric_only_when_plainly_numeric_and_keep_their_format()
    {
        var t = Tbl(1, 12, "1,234.50", "12,34,567", "-7", "3.14", "007", "9876543210", "౧౨౩", "Rs.5000", "12%", "=1+1", "1,23", "  8 ");
        var path = Tmp(".xlsx");
        try
        {
            XlsxExporter.Write(new (OcrPage, IReadOnlyList<OcrTable>)[] { (new OcrPage(0, 1, 1, Array.Empty<OcrLine>(), TimeSpan.Zero), new[] { t }) }, path);
            var s = ReadWorkbook(path, out _, out _)["Page 1 Table 1"];
            Assert.True(s["A1"].IsNumber); Assert.Equal("1234.5", s["A1"].Text); Assert.Equal("#,##0.00", s["A1"].Format);
            Assert.True(s["B1"].IsNumber); Assert.Equal("1234567", s["B1"].Text); Assert.Contains("##\\,##\\,##0", s["B1"].Format);
            Assert.True(s["C1"].IsNumber); Assert.Equal("-7", s["C1"].Text); Assert.Equal("0", s["C1"].Format);
            Assert.True(s["D1"].IsNumber); Assert.Equal("0.00", s["D1"].Format);
            foreach (var k in new[] { "E1", "F1", "G1", "H1", "I1", "J1", "K1", "L1" }) Assert.False(s[k].IsNumber, k);
            Assert.Equal("౧౨౩", s["G1"].Text);
            Assert.Equal("=1+1", s["J1"].Text);   // stored as a string, never a formula
            Assert.Equal("007", s["E1"].Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Xlsx_writes_merged_cells_and_survives_control_characters()
    {
        var cells = new List<OcrTableCell>
        {
            new(0, 0, 1, 2, "Title\u0001 here", new RectangleF()),
            new(1, 0, 1, 1, "a\nb", new RectangleF()), new(1, 1, 1, 1, "c", new RectangleF()),
        };
        var t = new OcrTable(new RectangleF(), 2, 2, cells);
        var path = Tmp(".xlsx");
        try
        {
            XlsxExporter.Write(new (OcrPage, IReadOnlyList<OcrTable>)[] { (new OcrPage(2, 1, 1, Array.Empty<OcrLine>(), TimeSpan.Zero), new[] { t }) }, path);
            var wb = ReadWorkbook(path, out var merges, out _);
            Assert.Equal(new[] { "A1:B1" }, merges["Page 3 Table 1"]);
            Assert.Equal("Title here", wb["Page 3 Table 1"]["A1"].Text);
            Assert.Equal("a\nb", wb["Page 3 Table 1"]["A2"].Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Xlsx_with_no_pages_still_has_a_sheet()
    {
        var path = Tmp(".xlsx");
        try
        {
            XlsxExporter.Write(Array.Empty<(OcrPage, IReadOnlyList<OcrTable>)>(), path);
            Assert.Single(ReadWorkbook(path, out _, out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Xlsx_keeps_the_lines_outside_tables_on_a_text_sheet()
    {
        // a title above the table and a note below it are not in the table: they go to "Page 1 text"; a line inside the table does not
        var table = new OcrTable(new RectangleF(100, 100, 400, 100), 1, 2, new[]
        {
            new OcrTableCell(0, 0, 1, 1, "name", new RectangleF(100, 100, 200, 100)), new OcrTableCell(0, 1, 1, 1, "42", new RectangleF(300, 100, 200, 100)),
        });
        var page = new OcrPage(0, 1000, 1000, new[]
        {
            Line("TITLE of the page", 100, 20, 300, 40), Line("name", 110, 120, 80, 40), Line("42", 310, 120, 40, 40), Line("Note: signed", 100, 400, 300, 40),
        }, TimeSpan.Zero);
        var allInside = new OcrPage(1, 1000, 1000, new[] { Line("name", 110, 120, 80, 40) }, TimeSpan.Zero);
        var path = Tmp(".xlsx");
        try
        {
            XlsxExporter.Write(new (OcrPage, IReadOnlyList<OcrTable>)[] { (page, new[] { table }), (allInside, new[] { table }) }, path);
            var wb = ReadWorkbook(path, out _, out _);
            Assert.Equal(new[] { "Page 1 Table 1", "Page 1 text", "Page 2 Table 1" }, wb.Keys.ToArray());
            Assert.Equal("TITLE of the page", wb["Page 1 text"]["A1"].Text);
            Assert.Equal("Note: signed", wb["Page 1 text"]["A2"].Text);
            Assert.Equal(2, wb["Page 1 text"].Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Xlsx_number_edge_cases_stay_text_when_a_number_would_lose_information()
    {
        var t = Tbl(1, 11, "+5", "1234567890.123456", "0.1234567890123456", "123456789.012345", "12,34,56,78,901", "1,00,00,00,000", "99,99,99,999", "-1,234.50", "1234567.891");
        var path = Tmp(".xlsx");
        try
        {
            XlsxExporter.Write(new (OcrPage, IReadOnlyList<OcrTable>)[] { (new OcrPage(0, 1, 1, Array.Empty<OcrLine>(), TimeSpan.Zero), new[] { t }) }, path);
            var s = ReadWorkbook(path, out _, out _)["Page 1 Table 1"];
            Assert.False(s["A1"].IsNumber); Assert.Equal("+5", s["A1"].Text);                 // sign kept as text
            Assert.False(s["B1"].IsNumber); Assert.Equal("1234567890.123456", s["B1"].Text);  // 16 significant digits
            Assert.False(s["C1"].IsNumber);                                                    // 16 significant digits after the leading zero
            Assert.True(s["D1"].IsNumber);                                                     // exactly 15 significant digits
            Assert.False(s["E1"].IsNumber); Assert.Equal("12,34,56,78,901", s["E1"].Text);    // Indian grouping beyond 99 crore
            Assert.False(s["F1"].IsNumber); Assert.Equal("1,00,00,00,000", s["F1"].Text);
            Assert.True(s["G1"].IsNumber); Assert.Equal("999999999", s["G1"].Text);
            Assert.Contains("##\\,##\\,##\\,##0", s["G1"].Format);
            Assert.True(s["H1"].IsNumber); Assert.Equal("-1234.5", s["H1"].Text);
            Assert.True(s["I1"].IsNumber); Assert.Equal("0.000", s["I1"].Format);
        }
        finally { File.Delete(path); }
    }

    // ---- model-backed end to end -------------------------------------------------------------------------------------

    const string TeluguFont = "NotoSansTelugu-Regular.ttf";

    /// <summary>Draws text with Latin runs in NotoSans and Indic runs in the Telugu font, baseline at y.</summary>
    static void Txt(SKCanvas c, string text, float x, float y, float size)
    {
        using var latinTf = SKTypeface.FromFile(TestEnv.Font("NotoSans-Regular.ttf"));
        using var teluguTf = SKTypeface.FromFile(TestEnv.Font(TeluguFont));
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        int i = 0;
        while (i < text.Length)
        {
            bool latin = text[i] < 0x0900;
            int j = i;
            while (j < text.Length && (text[j] < 0x0900) == latin) j++;
            var run = text[i..j];
            var tf = latin ? latinTf : teluguTf;
            using var font = new SKFont(tf, size);
            c.DrawShapedText(run, x, y, SKTextAlign.Left, font, paint);
            using var shaper = new SKShaper(tf);
            x += shaper.Shape(run, font).Width;
            i = j;
        }
    }

    static readonly string[] TeluguHeads = { "పేరు", "ఊరు", "వయస్సు", "మొత్తం", "తేదీ", "స్థితి" };
    static readonly string[] Names = { "Ravi Kumar", "Sita Devi", "Anil Reddy", "Lakshmi", "Venkat Rao", "Padma", "Suresh", "Kavitha", "Mohan Das" };

    static string[][] Data(int rows, int cols)
    {
        var d = new string[rows][];
        for (int r = 0; r < rows; r++)
        {
            d[r] = new string[cols];
            for (int c = 0; c < cols; c++)
                d[r][c] = r == 0 ? TeluguHeads[c % TeluguHeads.Length]
                        : c == 0 ? Names[(r - 1) % Names.Length]
                        : c % 2 == 1 ? (1000 * r + 37 * c).ToString() : $"{20 + r}-0{c}-2023";
        }
        return d;
    }

    static SKBitmap RenderTable(string[][] data, bool borders, out float[] xs, out float[] ys, float size = 34, float colW = 300, float rowH = 64, int mergeHeaderCols = 0)
    {
        int rows = data.Length, cols = data[0].Length;
        xs = Enumerable.Range(0, cols + 1).Select(i => 80 + colW * i).ToArray();
        ys = Enumerable.Range(0, rows + 1).Select(i => 80 + rowH * i).ToArray();
        var bmp = new SKBitmap((int)xs[^1] + 80, (int)ys[^1] + 80);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.White);
        if (borders)
        {
            foreach (var y in ys) HLine(c, y, xs[0], xs[^1], 3);
            for (int i = 0; i < xs.Length; i++)
                for (int r = 0; r < rows; r++)
                    if (r == 0 && mergeHeaderCols > 1 && i > 0 && i < xs.Length - 1) continue;
                    else VLine(c, xs[i], ys[r], ys[r + 1] + 1.5f, 3);
        }
        for (int r = 0; r < rows; r++)
            for (int col = 0; col < cols; col++)
            {
                if (r == 0 && mergeHeaderCols > 1 && col > 0) continue;
                Txt(c, data[r][col], xs[col] + 24, ys[r] + rowH * 0.68f, size);
            }
        c.Flush();
        return bmp;
    }

    static double CerOf(string expected, string actual) => TestEnv.Cer(expected, actual);

    void AssertCells(OcrTable t, string[][] data, double maxCerTelugu = 0.5, double maxCerLatin = 0.15)
    {
        Assert.Equal((data.Length, data[0].Length), (t.Rows, t.Columns));
        var grid = t.ToGrid();
        int bad = 0;
        for (int r = 0; r < data.Length; r++)
            for (int c = 0; c < data[0].Length; c++)
            {
                double cer = CerOf(data[r][c], grid[r, c]);
                bool indic = r == 0;
                if (cer > (indic ? maxCerTelugu : maxCerLatin)) { bad++; _out.WriteLine($"  [{r},{c}] expected '{data[r][c]}' got '{grid[r, c]}' (CER {cer:F2})"); }
            }
        Assert.True(bad == 0, $"{bad} of {data.Length * data[0].Length} cells read wrongly");
    }

    static readonly OcrLanguage[] TeEn = { OcrLanguage.Telugu, OcrLanguage.English };

    [ModelsFact]
    public void Bordered_Telugu_and_English_table_end_to_end_and_xlsx()
    {
        var data = Data(6, 4);
        using var bmp = RenderTable(data, borders: true, out _, out _);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, TeEn);
        var tables = TableExtractor.Extract(eng, bmp, page, TeEn);
        var t = Assert.Single(tables);
        Assert.True(t.HasRulings);
        AssertCells(t, data);

        var path = Tmp(".xlsx");
        try
        {
            XlsxExporter.Write(new (OcrPage, IReadOnlyList<OcrTable>)[] { (page, tables) }, path);
            var wb = ReadWorkbook(path, out _, out _);
            var s = wb["Page 1 Table 1"];
            Assert.Equal(24, s.Count);
            Assert.Equal(t.ToGrid()[0, 0], s["A1"].Text);
            Assert.Contains(s.Values, x => x.IsNumber);
        }
        finally { File.Delete(path); }
    }

    [ModelsFact]
    public void Table_cells_of_an_Auto_page_are_read_with_the_detected_languages()
    {
        var data = Data(6, 4);
        using var bmp = RenderTable(data, borders: true, out _, out _);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var auto = new[] { OcrLanguage.Auto };
        var page = eng.Recognize(bmp, auto);
        Assert.Contains(OcrLanguage.Telugu, page.DetectedLanguages);
        Assert.DoesNotContain(OcrLanguage.Auto, TableExtractor.ResolveCellLanguages(auto, page));
        var t = Assert.Single(TableExtractor.Extract(eng, bmp, page, auto));
        AssertCells(t, data);
    }

    [ModelsFact]
    public void Borderless_table_end_to_end()
    {
        var data = Data(6, 4);
        using var bmp = RenderTable(data, borders: false, out _, out _);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, TeEn);
        var t = Assert.Single(TableExtractor.Extract(eng, bmp, page, TeEn));
        Assert.False(t.HasRulings);
        AssertCells(t, data);
    }

    [ModelsFact]
    public void Merged_header_end_to_end()
    {
        var data = Data(6, 4);
        data[0] = new[] { "మొత్తం వివరాలు", "", "", "" };
        using var bmp = RenderTable(data, borders: true, out _, out _, mergeHeaderCols: 4);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, TeEn);
        var t = Assert.Single(TableExtractor.Extract(eng, bmp, page, TeEn));
        var head = t.Cells.Single(c => c.Row == 0);
        Assert.Equal(4, head.ColSpan);
        Assert.True(CerOf("మొత్తం వివరాలు", head.Text) < 0.5, head.Text);
        Assert.Equal(1 + 20, t.Cells.Count);
    }

    [ModelsFact]
    public void Label_value_form_end_to_end()
    {
        var rows = new (string L, string V)[] { ("Name", "Ravi Kumar"), ("Village", "Kukatpally"), ("Survey No", "123/4"), ("Mandal", "Medchal"), ("District", "Hyderabad") };
        using var bmp = new SKBitmap(1400, 520);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.White);
            for (int i = 0; i < rows.Length; i++)
            {
                Txt(c, rows[i].L, 100, 100 + 80 * i, 38);
                Txt(c, rows[i].V, 720, 100 + 80 * i, 38);
            }
        }
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, TeEn);
        var t = Assert.Single(TableExtractor.Extract(eng, bmp, page, TeEn));
        Assert.Equal((5, 2), (t.Rows, t.Columns));
        var g = t.ToGrid();
        for (int i = 0; i < rows.Length; i++)
        {
            Assert.True(CerOf(rows[i].L, g[i, 0]) < 0.2, g[i, 0]);
            Assert.True(CerOf(rows[i].V, g[i, 1]) < 0.2, g[i, 1]);
        }
    }

    [ModelsFact]
    public void Paragraph_page_has_no_table_and_xlsx_falls_back_to_a_text_sheet()
    {
        var lines = new[]
        {
            "తెలుగు భాష చాలా మధురమైనది ప్రతి పౌరుడు తన హక్కులను తెలుసుకోవాలి",
            "భారతదేశం నా మాతృభూమి భారతీయులందరూ నా సహోదరులు నేను నా దేశమును ప్రేమించుచున్నాను",
            "ఆంధ్ర ప్రదేశ్ ప్రభుత్వం ప్రజల సంక్షేమం కోసం అనేక పథకాలను అమలు చేస్తున్నది",
            "ప్రతి విద్యార్థి క్రమశిక్షణతో చదువుకొని దేశానికి మంచి పేరు తీసుకురావాలి",
        };
        using var bmp = TestEnv.Render(lines.Select(l => (l, TeluguFont)).ToList(), 2000, 420, 40);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, TeEn);
        var tables = TableExtractor.Extract(eng, bmp, page, TeEn);
        Assert.Empty(tables);

        var path = Tmp(".xlsx");
        try
        {
            XlsxExporter.Write(new (OcrPage, IReadOnlyList<OcrTable>)[] { (page, tables) }, path);
            var wb = ReadWorkbook(path, out _, out _);
            var s = Assert.Single(wb);
            Assert.Equal("Page 1 text", s.Key);
            Assert.Equal(page.Lines.Count, s.Value.Count);
        }
        finally { File.Delete(path); }
    }

    [ModelsFact]
    public void Extraction_time_on_a_10_by_6_table_page()
    {
        var data = Data(10, 6);
        // a variant where every row is a single wide detector box would be worst case; here the detector sees cells
        using var bmp = RenderTable(data, borders: true, out _, out _, size: 30, colW: 300, rowH: 60);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        eng.Recognize(bmp, TeEn); // warm up the sessions
        var sw = Stopwatch.StartNew();
        var page = eng.Recognize(bmp, TeEn);
        var ocr = sw.Elapsed;
        sw.Restart();
        var tables = TableExtractor.Extract(eng, bmp, page, TeEn);
        var ext = sw.Elapsed;
        var t = Assert.Single(tables);
        _out.WriteLine($"page {bmp.Width}x{bmp.Height}: OCR {ocr.TotalMilliseconds:F0} ms, table extraction {ext.TotalMilliseconds:F0} ms, {t.Rows}x{t.Columns}, {page.Lines.Count} lines");
        Assert.Equal((10, 6), (t.Rows, t.Columns));

        using var borderless = RenderTable(data, borders: false, out _, out _, size: 30, colW: 300, rowH: 60);
        var page2 = eng.Recognize(borderless, TeEn);
        sw.Restart();
        var t2 = Assert.Single(TableExtractor.Extract(eng, borderless, page2, TeEn));
        _out.WriteLine($"borderless: extraction {sw.Elapsed.TotalMilliseconds:F0} ms, {t2.Rows}x{t2.Columns}");
    }

    // ---- regression renders from the v2 review: layouts that are not tables ----------------------------------------------

    const string Para = "ఈ దస్తావేజు ప్రకారం ఆస్తి హక్కులు బదిలీ చేయబడును మరియు రెవెన్యూ రికార్డులలో నమోదు చేయబడును";

    /// <summary>Draws rows of (x, text) pieces, 65 px apart from y = 250, on a 2480-wide page, then optionally an extra drawing.</summary>
    static SKBitmap RenderLayout(IReadOnlyList<(float X, string T)[]> rows, Action<SKCanvas>? extra = null, int height = 1600)
    {
        var bmp = new SKBitmap(2480, height);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.White);
        float y = 250;
        foreach (var row in rows) { foreach (var (x, t) in row) if (t.Length > 0) Txt(c, t, x, y, 38); y += 65; }
        extra?.Invoke(c);
        c.Flush();
        return bmp;
    }

    [ModelsFact]
    public void Numbered_paragraphs_signature_blocks_short_pairs_and_framed_certificates_are_not_tables()
    {
        var cases = new Dictionary<string, (List<(float, string)[]> Rows, Action<SKCanvas>? Extra)>
        {
            ["numbered_english"] = (new()
            {
                new[] { (150f, "1."), (300f, "The petitioner is the absolute owner of the land in Survey No. 45") },
                new[] { (300f, "situated at Gachibowli village and has been in possession.") },
                new[] { (150f, "2."), (300f, "The respondent has no right or title over the said property and") },
                new[] { (300f, "is trying to interfere with the peaceful possession.") },
                new[] { (150f, "3."), (300f, "It is therefore prayed that this Hon'ble Court may be pleased") },
                new[] { (300f, "to grant a permanent injunction against the respondent.") },
                new[] { (150f, "4."), (300f, "The petitioner undertakes to abide by any condition imposed.") },
            }, null),
            ["numbered_telugu"] = (Enumerable.Range(1, 4).Select(i => new[] { (150f, $"{i}."), (300f, Para) }).ToList(), null),
            ["two_column_short_phrases"] = (new[]
            {
                ("తెలుగు భాష చాలా మధురమైనది", "హైదరాబాద్ తెలంగాణ రాజధాని"), ("భారతదేశం నా మాతృభూమి", "రైతులకు సహాయం అందిస్తుంది"),
                ("ఆంధ్ర ప్రదేశ్ ప్రభుత్వం", "ప్రతి పౌరుడు హక్కులు"), ("తెలుగు భాష చాలా మధురమైనది", "హైదరాబాద్ తెలంగాణ రాజధాని"),
            }.Select(r => new[] { (150f, r.Item1), (1300f, r.Item2) }).ToList(), null),
            ["letter_with_signature_block"] = (new()
            {
                new[] { (150f, "To,") }, new[] { (150f, "The Tahsildar,") }, new[] { (150f, "Serilingampally Mandal, Hyderabad.") }, new[] { (150f, "") },
                new[] { (150f, "Sub: Request for issue of pahani copy for Survey No. 123/4A") }, new[] { (150f, "") },
                new[] { (150f, Para) }, new[] { (150f, Para) }, new[] { (150f, "Kindly issue the certified copy at the earliest.") }, new[] { (150f, "") },
                new[] { (150f, "Place: Hyderabad"), (1600f, "Yours faithfully,") }, new[] { (150f, "Date: 12-05-2024"), (1600f, "(K. Rama Rao)") },
                new[] { (150f, "Encl: Aadhaar copy"), (1600f, "Advocate") }, new[] { (150f, "Ph: 040-2345678"), (1600f, "Enrol No. AP/123/2001") },
            }, null),
            ["framed_certificate"] = (new()
            {
                new[] { (900f, "CERTIFICATE") }, new[] { (150f, "") }, new[] { (150f, Para) }, new[] { (150f, Para) }, new[] { (150f, Para) }, new[] { (150f, "Issued under the Revenue Act.") },
            }, c =>
            {
                using var p = new SKPaint { Color = SKColors.Black, StrokeWidth = 4, Style = SKPaintStyle.Stroke };
                c.DrawRect(100, 150, 2280, 1100, p); c.DrawLine(100, 300, 2380, 300, p);
            }),
        };
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var all = new List<(OcrPage, IReadOnlyList<OcrTable>)>();
        int pi = 0;
        foreach (var (name, (rows, extra)) in cases)
        {
            using var bmp = RenderLayout(rows, extra);
            var page = eng.Recognize(bmp, TeEn) with { PageIndex = pi++ };
            var tables = TableExtractor.Extract(eng, bmp, page, TeEn);
            _out.WriteLine($"{name}: {page.Lines.Count} lines, {tables.Count} tables");
            Assert.True(tables.Count == 0, $"{name}: {tables.Count} table(s) found, first {tables.FirstOrDefault()?.Rows}x{tables.FirstOrDefault()?.Columns}");
            all.Add((page, tables));
        }
        // nothing is lost in the workbook: each page has a text sheet with all its lines
        var path = Tmp(".xlsx");
        try
        {
            XlsxExporter.Write(all, path);
            var wb = ReadWorkbook(path, out _, out _);
            Assert.Equal(cases.Count, wb.Count);
            foreach (var (page, _) in all) Assert.Equal(page.Lines.Count(l => l.Text.Length > 0), wb[$"Page {page.PageIndex + 1} text"].Count);
        }
        finally { File.Delete(path); }
    }

    [ModelsFact]
    public void A_table_with_text_above_and_below_keeps_that_text_on_a_text_sheet()
    {
        var data = Data(5, 3);
        using var bmp = RenderTable(data, borders: true, out _, out var ys);
        // add a title and a note on a taller canvas, outside the table
        using var tall = new SKBitmap(bmp.Width, bmp.Height + 400);
        using (var c = new SKCanvas(tall))
        {
            c.Clear(SKColors.White);
            using (var im = SKImage.FromBitmap(bmp)) c.DrawImage(im, 0, 200, new SKSamplingOptions(), null);
            Txt(c, "Statement of dues as on date", 80, 120, 40);
            Txt(c, "Prepared by the accounts section", 80, tall.Height - 100, 40);
        }
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(tall, TeEn);
        var tables = TableExtractor.Extract(eng, tall, page, TeEn);
        Assert.Single(tables);
        var path = Tmp(".xlsx");
        try
        {
            XlsxExporter.Write(new (OcrPage, IReadOnlyList<OcrTable>)[] { (page, tables) }, path);
            var wb = ReadWorkbook(path, out _, out _);
            Assert.Equal(new[] { "Page 1 Table 1", "Page 1 text" }, wb.Keys.ToArray());
            var text = string.Join("\n", wb["Page 1 text"].Values.Select(v => v.Text));
            _out.WriteLine(text);
            Assert.True(CerOf("Statement of dues as on date", wb["Page 1 text"]["A1"].Text) < 0.2);
            Assert.True(CerOf("Prepared by the accounts section", wb["Page 1 text"]["A2"].Text) < 0.2);
        }
        finally { File.Delete(path); }
    }
}
