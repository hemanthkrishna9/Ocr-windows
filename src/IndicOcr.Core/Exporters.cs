using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace IndicOcr.Core;

public static class TextExporter
{
    /// <summary>Writes UTF-8 (with BOM, so old Notepad detects it); pages separated by a blank line.</summary>
    public static void Write(IEnumerable<OcrPage> pages, string path)
    {
        ArgumentNullException.ThrowIfNull(pages);
        EnsureDirectory(path);
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        bool first = true;
        foreach (var p in pages)
        {
            if (!first) w.Write("\n\n");
            first = false;
            w.Write(p.Text);
        }
        w.Write('\n');
    }

    internal static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}

public static class DocxExporter
{
    /// <summary>One paragraph per OCR line; page breaks between pages. Indic text uses the Nirmala UI complex-script font.</summary>
    public static void Write(IEnumerable<OcrPage> pages, string path)
    {
        ArgumentNullException.ThrowIfNull(pages);
        TextExporter.EnsureDirectory(path);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);

        bool first = true;
        foreach (var page in pages)
        {
            if (!first) body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
            first = false;
            foreach (var line in page.Lines)
            {
                bool indic = line.Language != OcrLanguage.English || line.Text.Any(c => c >= 'ऀ');
                var rp = new RunProperties(new RunFonts
                {
                    Ascii = indic ? "Nirmala UI" : "Calibri",
                    HighAnsi = indic ? "Nirmala UI" : "Calibri",
                    ComplexScript = "Nirmala UI",
                });
                body.Append(new Paragraph(new Run(rp, new Text(line.Text) { Space = SpaceProcessingModeValues.Preserve })));
            }
        }
        if (!body.HasChildren) body.Append(new Paragraph());
        main.Document.Save();
    }
}
