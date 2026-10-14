using System.Drawing;
using IndicOcr.Core.Internal;
using SkiaSharp;

namespace IndicOcr.Core;

/// <summary>One cell of a detected table. Row and Column are 0-based; a merged cell has RowSpan or ColSpan above 1 and is reported once, at its top-left grid position.</summary>
public sealed record OcrTableCell(int Row, int Column, int RowSpan, int ColSpan, string Text, RectangleF Box);

/// <summary>
/// A table found on a page. Bounds and every cell Box are in page pixel coordinates. Cells holds every grid position exactly once
/// (empty cells have empty Text); positions covered by a merged cell do not appear separately.
/// </summary>
public sealed record OcrTable(RectangleF Bounds, int Rows, int Columns, IReadOnlyList<OcrTableCell> Cells)
{
    /// <summary>True when the grid came from ruling lines in the image; false when it was inferred from text alignment.</summary>
    public bool HasRulings { get; init; }

    /// <summary>Rows x Columns grid of cell texts. A merged cell's text sits at its top-left position; the other positions it covers are empty.</summary>
    public string[,] ToGrid()
    {
        var g = new string[Rows, Columns];
        for (int r = 0; r < Rows; r++) for (int c = 0; c < Columns; c++) g[r, c] = "";
        foreach (var cell in Cells)
            if (cell.Row >= 0 && cell.Row < Rows && cell.Column >= 0 && cell.Column < Columns) g[cell.Row, cell.Column] = cell.Text;
        return g;
    }
}

/// <summary>
/// Finds tables on a recognised page. Bordered tables are found from long horizontal and vertical ruling lines in the image
/// (including merged cells where a border is missing); borderless tables and label/value forms are inferred from the alignment of
/// the recognised text (three or more rows that share two or more column gutters). Ruled tables take precedence: text inside one is
/// never considered for the alignment path. Pure local computation; no network.
/// </summary>
public static class TableExtractor
{
    /// <summary>
    /// Extracts tables from <paramref name="page"/>. <paramref name="image"/> must be the bitmap the page boxes refer to (for a page the
    /// engine turned upright, the bitmap from <see cref="OcrEngine.RecognizeOriented"/> or <see cref="OcrEngine.RecognizeFileAsync"/>).
    /// Lines that cross a cell border are split and each part is read again with <see cref="OcrEngine.RecognizeLine"/>; every other cell
    /// reuses the page's line text, so the cost is only a few extra single-line reads. With <see cref="OcrLanguage.Auto"/> in
    /// <paramref name="langs"/> those reads use the page's <see cref="OcrPage.DetectedLanguages"/> instead of a new script vote per cell.
    /// </summary>
    public static IReadOnlyList<OcrTable> Extract(OcrEngine engine, SKBitmap image, OcrPage page, IReadOnlyList<OcrLanguage> langs)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(langs);
        // Auto would run a script vote for every cell: the page already knows its scripts, so cells are read with those directly.
        var cellLangs = ResolveCellLanguages(langs, page);
        return TableBuilder.Build(image, page, box => engine.RecognizeLine(image, box, cellLangs).Text);
    }

    /// <summary>The languages for re-reading a cell: <paramref name="langs"/>, with Auto replaced by the page's detected languages when it has any.</summary>
    internal static IReadOnlyList<OcrLanguage> ResolveCellLanguages(IReadOnlyList<OcrLanguage> langs, OcrPage page)
    {
        if (!langs.Contains(OcrLanguage.Auto)) return langs;
        var resolved = page.DetectedLanguages.Where(l => l != OcrLanguage.Auto).Distinct().ToList();
        return resolved.Count > 0 ? resolved : langs;
    }

    /// <summary>
    /// Same detection without an engine. A line that crosses a cell border or has an internal gap cannot be re-read, so its whole text goes
    /// into the cell that holds most of it (the first cell of its row part). Prefer the engine overload.
    /// </summary>
    public static IReadOnlyList<OcrTable> Extract(SKBitmap image, OcrPage page) => TableBuilder.Build(image, page, null);
}
