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

    static string PdfText(string pdf)
    {
        var psi = new ProcessStartInfo("pdftotext", $"-enc UTF-8 \"{pdf}\" -") { RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        var s = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return s;
    }

    [FontsFact]
    public void Searchable_pdf_has_invisible_text_layer() => BuildSearchablePdf(out _);

    [PdftotextFact]
    public void Searchable_pdf_text_extracts_as_indic_unicode()
    {
        var path = BuildSearchablePdf(out var langs);
        var text = PdfText(path);
        foreach (var l in langs)
            Assert.Contains(l.Text, text);
    }

    string BuildSearchablePdf(out (string Text, string File, OcrLanguage L)[] langs)
    {
        langs = new (string Text, string File, OcrLanguage L)[]
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

        return path;
    }
}

public class NoNetworkTests
{
    static readonly string[] BannedAssemblies = { "System.Net.Http", "System.Net.Sockets", "System.Net.WebClient", "System.Net.Requests", "System.Net.WebSockets", "System.Net.WebSockets.Client", "System.Net.Mail" };

    static readonly string[] BannedTypes = { "HttpClient", "HttpWebRequest", "WebClient", "WebRequest", "Socket", "TcpClient", "UdpClient", "NamedPipeClientStream" };

    /// <summary>
    /// Assemblies that ship next to Core and contain System.Net.Http code that Core never calls. Each needs a written reason; keep this list short.
    /// PDFsharp 6.2.4 ships them inside the same NuGet package and the runtime assets cannot be excluded per file.
    /// </summary>
    static readonly Dictionary<string, string> DormantNetworkCode = new()
    {
        // The HTTP client in here is an RFC 3161 time-stamp-authority client for PDF digital signatures. Core only writes new,
        // unsigned, unencrypted PDFs; Core_source_does_not_use_pdf_security_apis enforces that no signing/security API is used.
        ["PdfSharp.Cryptography"] = "time-stamp HTTP client for PDF signing; Core never signs or encrypts",
        // PDFsharp test-support helpers; they reference System.Net.Http, but no Core code references any of their types.
        ["PdfSharp.Quality"] = "PDFsharp test-support helpers; no Core code references it",
    };

    /// <summary>Test-only assemblies in the test output folder (test platform, xunit, coverage, HarfBuzz text shaping used to draw test pages).</summary>
    static bool IsTestOnly(string name) =>
        name == "IndicOcr.Core.Tests" || name == "testhost" || name == "Newtonsoft.Json" || name == "HarfBuzzSharp" || name == "SkiaSharp.HarfBuzz"
        || name.StartsWith("Microsoft.TestPlatform", StringComparison.Ordinal) || name.StartsWith("Microsoft.VisualStudio.", StringComparison.Ordinal)
        || name.StartsWith("xunit", StringComparison.Ordinal) || name.StartsWith("NuGet.", StringComparison.Ordinal)
        || name.StartsWith("coverlet", StringComparison.Ordinal);

    /// <summary>
    /// Every managed DLL in the output folder that ships with Core: all files except test-only ones. (The BCL is not copied to the output.)
    /// Scanning the folder, not just the reference closure, also covers assemblies that are loaded by reflection (PdfSharp.Cryptography).
    /// </summary>
    static List<string> ShippedAssemblies()
    {
        string dir = Path.GetDirectoryName(typeof(OcrEngine).Assembly.Location)!;
        return Directory.EnumerateFiles(dir, "*.dll")
            .Where(f => !IsTestOnly(Path.GetFileNameWithoutExtension(f)))
            .Where(f => { try { using var fs = File.OpenRead(f); using var pe = new PEReader(fs); return pe.HasMetadata && pe.GetMetadataReader().IsAssembly; } catch { return false; } })
            .ToList();
    }

    [Fact]
    public void Core_does_not_reference_network_assemblies_or_types()
    {
        var asm = typeof(OcrEngine).Assembly;
        var refs = asm.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        foreach (var banned in BannedAssemblies) Assert.DoesNotContain(banned, refs);
        AssertNoNetworkTypes(asm.Location);
    }

    [Fact]
    public void Every_managed_dll_shipped_with_Core_has_no_network_references()
    {
        var files = ShippedAssemblies();
        Assert.True(files.Count >= 8, "closure scan found only " + files.Count + " assemblies");
        var names = files.Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
        Assert.Contains("PdfSharp", names);
        Assert.Contains("Microsoft.ML.OnnxRuntime", names);
        foreach (var f in files)
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (DormantNetworkCode.ContainsKey(name)) continue; // allow-listed with a written reason above
            AssertNoNetworkTypes(f);
        }
    }

    [Fact]
    public void Allow_list_is_small_and_every_entry_has_a_reason()
    {
        // The allow-list must not grow silently: every entry needs a reason, and an entry whose file is gone should be dropped.
        foreach (var (name, reason) in DormantNetworkCode) Assert.False(string.IsNullOrWhiteSpace(reason), name);
        Assert.True(DormantNetworkCode.Count <= 2, "do not grow the allow-list without a new written reason");
    }

    [Fact]
    public void PDFsharp_is_the_pinned_6_2_line()
    {
        var v = typeof(PdfSharp.Pdf.PdfDocument).Assembly.GetName().Version!;
        Assert.Equal(6, v.Major);
        Assert.Equal(2, v.Minor);
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(PdfSharp.Pdf.PdfDocument).Assembly.Location);
        Assert.StartsWith("6.2.", info.FileVersion);
    }

    static void AssertNoNetworkTypes(string path)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) return;
        var md = pe.GetMetadataReader();
        string asmName = Path.GetFileName(path);
        foreach (var h in md.AssemblyReferences)
        {
            var refName = md.GetString(md.GetAssemblyReference(h).Name);
            Assert.False(BannedAssemblies.Contains(refName), $"{asmName} references {refName}");
        }
        foreach (var h in md.TypeReferences)
        {
            var tr = md.GetTypeReference(h);
            string name = md.GetString(tr.Name), ns = md.GetString(tr.Namespace);
            if (ns.StartsWith("System.Net", StringComparison.Ordinal) || ns.StartsWith("System.IO.Pipes", StringComparison.Ordinal))
                Assert.False(BannedTypes.Contains(name), $"{asmName} uses {ns}.{name}");
            Assert.False(ns.StartsWith("System.Net.Http", StringComparison.Ordinal) || ns.StartsWith("System.Net.Sockets", StringComparison.Ordinal), $"{asmName}: {ns}.{name}");
        }
    }

    [Fact]
    public void Core_source_does_not_use_pdf_security_apis()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "IndicOcr.Core"))) dir = Path.GetDirectoryName(dir);
        Assert.True(dir != null, "repository source folder not found");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(dir!, "src", "IndicOcr.Core"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var s = File.ReadAllText(f);
            foreach (var w in new[] { "SecuritySettings", "PdfSignature", "DigitalSignature", "UserPassword", "OwnerPassword", "TimeStampAuthority" })
                Assert.DoesNotContain(w, s);
        }
    }

    [Fact]
    public void Core_source_has_no_network_apis()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "IndicOcr.Core"))) dir = Path.GetDirectoryName(dir);
        Assert.True(dir != null, "repository source folder src/IndicOcr.Core not found above " + AppContext.BaseDirectory);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(dir!, "src", "IndicOcr.Core"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var s = File.ReadAllText(f);
            foreach (var w in new[] { "HttpClient", "WebClient", "System.Net.Sockets", "System.Net.Http", "WebRequest" })
                Assert.DoesNotContain(w, s);
        }
    }
}
