using System.Diagnostics;
using System.Drawing;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using DocumentFormat.OpenXml.Packaging;
using SkiaSharp;
using Xunit;

namespace IndicOcr.Core.Tests;

public class ExporterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "indicocr_exp_" + Guid.NewGuid().ToString("N"));
    public ExporterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    internal static OcrPage SamplePage(int index = 0)
    {
        var lines = new List<OcrLine>
        {
            new("తెలుగు భాష", 0.9f, OcrLanguage.Telugu, new RectangleF(100, 100, 600, 60), Array.Empty<PointF>()),
            new("Hello world 123", 0.95f, OcrLanguage.English, new RectangleF(100, 200, 700, 60), Array.Empty<PointF>()),
        };
        return new OcrPage(index, 1000, 800, lines, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void OcrPage_Text_joins_lines_with_newline()
        => Assert.Equal("తెలుగు భాష\nHello world 123", SamplePage().Text);

    [Fact]
    public void Text_export_roundtrips_unicode()
    {
        var path = Path.Combine(_dir, "o.txt");
        TextExporter.Write(new[] { SamplePage(0), SamplePage(1) }, path);
        var s = File.ReadAllText(path);
        Assert.Contains("తెలుగు భాష", s);
        Assert.Contains("Hello world 123", s);
        Assert.Equal(2, s.Split("తెలుగు").Length - 1);
    }

    [Fact]
    public void Docx_opens_and_contains_the_text()
    {
        var path = Path.Combine(_dir, "o.docx");
        DocxExporter.Write(new[] { SamplePage(0), SamplePage(1) }, path);
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        var text = body.InnerText;
        Assert.Contains("తెలుగు భాష", text);
        Assert.Contains("Hello world 123", text);
        Assert.Equal(2, body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Break>().Count() + 1);
    }

    static bool HavePdftotext()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pdftotext", "-v") { RedirectStandardError = true, RedirectStandardOutput = true });
            p?.WaitForExit(5000);
            return true;
        }
        catch { return false; }
    }

    static string PdfText(string pdf)
    {
        var psi = new ProcessStartInfo("pdftotext", $"-enc UTF-8 \"{pdf}\" -") { RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        var s = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return s;
    }

    [FontsFact]
    public void Searchable_pdf_has_invisible_text_layer_and_extracts_indic_unicode()
    {
        var langs = new (string Text, string File, OcrLanguage L)[]
        {
            ("తెలుగు భాష చాలా మధురమైనది", "NotoSansTelugu-Regular.ttf", OcrLanguage.Telugu),
            ("भारत मेरा देश है", "NotoSansDevanagari-Regular.ttf", OcrLanguage.Hindi),
            ("தமிழ் மொழி இனிமையானது", "NotoSansTamil-Regular.ttf", OcrLanguage.Tamil),
            ("ಕನ್ನಡ ಭಾಷೆ ಸುಂದರವಾಗಿದೆ", "NotoSansKannada-Regular.ttf", OcrLanguage.Kannada),
            ("Invoice 2024 total", "NotoSans-Regular.ttf", OcrLanguage.English),
        };
        using var bmp = TestEnv.Render(langs.Select(l => (l.Text, l.File)).ToList(), 1240, 900, 50);
        var lines = new List<OcrLine>();
        float y = 80 - 50 * 0.2f;
        foreach (var l in langs)
        {
            lines.Add(new OcrLine(l.Text, 1f, l.L, new RectangleF(60, y, 900, 60), Array.Empty<PointF>()));
            y += 80;
        }
        var page = new OcrPage(0, bmp.Width, bmp.Height, lines, TimeSpan.Zero);
        var path = Path.Combine(_dir, "o.pdf");
        SearchablePdfExporter.Write(new[] { (bmp, page) }, path, TestEnv.FontsDir);

        Assert.True(new FileInfo(path).Length > 1000);
        // structure: text render mode 3 present
        var raw = File.ReadAllBytes(path);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(raw, 0, 4));
        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            Assert.Equal(1, doc.PageCount);
            var content = doc.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue;
            var s = System.Text.Encoding.Latin1.GetString(content);
            Assert.Contains("3 Tr", s);
        }

        if (!HavePdftotext()) return; // extraction check needs poppler; structure already verified
        var text = PdfText(path);
        foreach (var l in langs)
            Assert.Contains(l.Text, text);
    }
}

public class NoNetworkTests
{
    static readonly string[] BannedAssemblies = { "System.Net.Http", "System.Net.Sockets", "System.Net.WebClient", "System.Net.Requests", "System.Net.WebSockets", "System.Net.WebSockets.Client", "System.Net.Mail" };

    [Fact]
    public void Core_does_not_reference_network_assemblies_or_types()
    {
        var asm = typeof(OcrEngine).Assembly;
        var refs = asm.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        foreach (var banned in BannedAssemblies) Assert.DoesNotContain(banned, refs);

        using var fs = File.OpenRead(asm.Location);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var bannedTypes = new[] { "HttpClient", "HttpWebRequest", "WebClient", "WebRequest", "Socket", "TcpClient", "UdpClient", "NamedPipeClientStream" };
        foreach (var h in md.TypeReferences)
        {
            var tr = md.GetTypeReference(h);
            string name = md.GetString(tr.Name), ns = md.GetString(tr.Namespace);
            if (ns.StartsWith("System.Net", StringComparison.Ordinal) || ns.StartsWith("System.IO.Pipes", StringComparison.Ordinal))
                Assert.DoesNotContain(name, bannedTypes);
            Assert.False(ns.StartsWith("System.Net.Http", StringComparison.Ordinal) || ns.StartsWith("System.Net.Sockets", StringComparison.Ordinal), $"{ns}.{name}");
        }
    }

    [Fact]
    public void Core_source_has_no_network_apis()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "IndicOcr.Core"))) dir = Path.GetDirectoryName(dir);
        if (dir == null) return;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(dir, "src", "IndicOcr.Core"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var s = File.ReadAllText(f);
            foreach (var w in new[] { "HttpClient", "WebClient", "System.Net.Sockets", "System.Net.Http", "WebRequest" })
                Assert.DoesNotContain(w, s);
        }
    }
}
