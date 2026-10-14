using System.Buffers.Binary;
using System.Drawing;
using BitMiracle.LibTiff.Classic;
using IndicOcr.Core.Internal;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace IndicOcr.Core.Tests;

public class CpuTopologyTests
{
    [Fact]
    public void Smt_siblings_count_as_one_core_in_proc_cpuinfo()
    {
        // 2 physical cores x 2 hyper-threads, one socket
        var lines = new[]
        {
            "processor : 0", "physical id : 0", "core id : 0", "",
            "processor : 1", "physical id : 0", "core id : 1", "",
            "processor : 2", "physical id : 0", "core id : 0", "",
            "processor : 3", "physical id : 0", "core id : 1", "",
        };
        Assert.Equal(2, CpuTopology.CountFromCpuInfo(lines));
    }

    [Fact]
    public void Two_sockets_with_the_same_core_ids_are_distinct_cores()
    {
        var lines = new[] { "physical id : 0", "core id : 0", "", "physical id : 1", "core id : 0", "" };
        Assert.Equal(2, CpuTopology.CountFromCpuInfo(lines));
    }

    [Fact]
    public void Sysfs_topology_is_preferred_and_offline_cpus_are_skipped()
    {
        var root = Path.Combine(Path.GetTempPath(), "indicocr_cpu_" + Guid.NewGuid().ToString("N"));
        try
        {
            void Cpu(int n, string core, string pkg, string? online = null)
            {
                var d = Path.Combine(root, "cpu" + n, "topology");
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "core_id"), core + "\n");
                File.WriteAllText(Path.Combine(d, "physical_package_id"), pkg + "\n");
                if (online != null) File.WriteAllText(Path.Combine(root, "cpu" + n, "online"), online + "\n");
            }
            Cpu(0, "0", "0"); Cpu(1, "1", "0"); Cpu(2, "0", "0"); Cpu(3, "1", "0"); Cpu(4, "2", "0", online: "0");
            Directory.CreateDirectory(Path.Combine(root, "cpufreq")); // not a cpuN directory
            Assert.Equal(2, CpuTopology.LinuxPhysicalCores(root, "/nonexistent"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Default_thread_count_is_physical_cores_within_1_to_8()
    {
        Assert.InRange(CpuTopology.PhysicalCores, 1, Environment.ProcessorCount);
        Assert.Equal(Math.Clamp(CpuTopology.PhysicalCores, 1, 8), SessionFactory.DefaultThreads);
    }
}

public class DecodeScoreTests
{
    static float[] Frames(int c, params (int Cls, float P)[] f)
    {
        var d = new float[f.Length * c];
        for (int i = 0; i < f.Length; i++)
            for (int k = 0; k < c; k++) d[i * c + k] = k == f[i].Cls ? f[i].P : (1 - f[i].P) / (c - 1);
        return d;
    }

    [Fact]
    public void A_reading_that_drops_characters_has_less_mass_than_one_that_keeps_them()
    {
        var t = CharTable.FromLines(new[] { "a", "b", "c" });
        var full = CtcDecoder.Decode(Frames(5, (1, .95f), (0, .99f), (2, .95f), (0, .99f), (3, .95f)), 5, 5, t);
        var dropped = CtcDecoder.Decode(Frames(5, (1, .95f), (0, .99f), (0, .99f), (0, .99f), (0, .99f)), 5, 5, t);
        Assert.Equal("abc", full.Text);
        Assert.Equal("a", dropped.Text);
        Assert.True(full.Mass > dropped.Mass + 1.5f);
        // confidence alone (mean over kept characters) cannot tell them apart - that was the bug
        Assert.True(dropped.Confidence >= full.Confidence - 1e-6);
    }

    [Fact]
    public void Garbled_characters_near_one_half_earn_almost_nothing()
    {
        var t = CharTable.FromLines(new[] { "a", "b" });
        var r = CtcDecoder.Decode(Frames(4, (1, .5f), (0, .9f), (2, .5f)), 3, 4, t);
        Assert.Equal(0.0, (double)r.Mass, 3);
    }
}

public class MixedTokenFixTests
{
    [Theory]
    [InlineData("Sy.Nం.12/A", "Sy.No.12/A")]            // survey number: anusvara for a round 'o'
    [InlineData("Sy.No.12/A", "Sy.No.12/A")]            // already right
    [InlineData("Rs.5೦೦೦", "Rs.5000")]                  // Kannada zeros
    [InlineData("Rs.5౦౦౦/-", "Rs.5000/-")]              // Telugu zeros
    [InlineData("AP09CX1234", "AP09CX1234")]
    [InlineData("APం9CX1234", "AP09CX1234")]            // vehicle number, zero between letters and digit
    [InlineData("ABCDEం234F", "ABCDE0234F")]            // PAN: next char is a digit
    [InlineData("ABCంDE1234F", "ABCODE1234F")]          // PAN, upper-case run
    [InlineData("TS09EA१२३४", "TS09EA१२३४")]            // other Indic digits: leave alone (not a zero)
    [InlineData("12-05-2023 ఽ", "12-05-2023 ఽ")]
    public void Identifier_tokens(string input, string expected) => Assert.Equal(expected, MixedTokenFix.Apply(input));

    [Theory]
    [InlineData("మండలం: Kukatpally, జల్లా: Medchal")]    // real Telugu words with anusvara are never touched
    [InlineData("తేదీ 12-05-2023 Rs.5000")]
    [InlineData("ఆధార్ సంఖ్య 1234 5678 9012")]
    [InlineData("ABCంకి")]                               // token mixes Latin with a real Indic letter: unchanged
    [InlineData("Noం")]                                  // fewer than two ... (N,o = 2 letters) -> only the suspect glyph maps
    public void Real_text_is_untouched(string input)
    {
        var fixedText = MixedTokenFix.Apply(input);
        if (input == "Noం") Assert.Equal("Noo", fixedText);
        else Assert.Equal(input, fixedText);
    }

    [Fact]
    public void Single_letter_tokens_are_not_eligible() => Assert.Equal("Aం", MixedTokenFix.Apply("Aం"));
}

public class ColumnOrderingTests
{
    static RectangleF R(float x, float y, float w = 500, float h = 50) => new(x, y, w, h);

    [Fact]
    public void Two_columns_are_read_column_by_column()
    {
        var boxes = new List<(string, RectangleF)>();
        for (int i = 0; i < 6; i++) { boxes.Add(($"L{i}", R(100, 100 + i * 80))); boxes.Add(($"R{i}", R(900, 100 + i * 80))); }
        var order = LineOrdering.Order(boxes, b => b.Item2).Select(b => b.Item1).ToArray();
        Assert.Equal(new[] { "L0", "L1", "L2", "L3", "L4", "L5", "R0", "R1", "R2", "R3", "R4", "R5" }, order);
    }

    [Fact]
    public void A_full_width_heading_splits_the_page_into_bands()
    {
        var boxes = new List<(string, RectangleF)> { ("H1", R(100, 20, 1300)) };
        for (int i = 0; i < 4; i++) { boxes.Add(($"A-L{i}", R(100, 100 + i * 80))); boxes.Add(($"A-R{i}", R(900, 100 + i * 80))); }
        boxes.Add(("H2", R(100, 450, 1300)));
        for (int i = 0; i < 3; i++) { boxes.Add(($"B-L{i}", R(100, 530 + i * 80))); boxes.Add(($"B-R{i}", R(900, 530 + i * 80))); }
        var order = LineOrdering.Order(boxes, b => b.Item2).Select(b => b.Item1).ToArray();
        Assert.Equal(new[] { "H1", "A-L0", "A-L1", "A-L2", "A-L3", "A-R0", "A-R1", "A-R2", "A-R3", "H2", "B-L0", "B-L1", "B-L2", "B-R0", "B-R1", "B-R2" }, order);
    }

    [Fact]
    public void Single_column_keeps_plain_top_to_bottom_order()
    {
        var boxes = Enumerable.Range(0, 10).Select(i => ($"S{i}", R(100, 100 + i * 80, i % 2 == 0 ? 900 : 500))).Reverse().ToList();
        var order = LineOrdering.Order(boxes, b => b.Item2).Select(b => b.Item1).ToArray();
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"S{i}").ToArray(), order);
    }

    [Fact]
    public void Label_value_table_rows_are_not_mistaken_for_columns()
    {
        // narrow labels on the left, wide values on the right: reading row by row is right
        var boxes = new List<(string, RectangleF)>();
        for (int i = 0; i < 8; i++) { boxes.Add(($"label{i}", R(100, 100 + i * 80, 200))); boxes.Add(($"value{i}", R(700, 100 + i * 80, 1200))); }
        var order = LineOrdering.Order(boxes, b => b.Item2).Select(b => b.Item1).ToArray();
        Assert.Equal("label0", order[0]);
        Assert.Equal("value0", order[1]);
        Assert.Equal("label1", order[2]);
    }
}

public class OrientationGeometryTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Rotate_and_MapQuad_agree_on_where_a_point_goes(int turns)
    {
        using var src = new SKBitmap(40, 20);
        using (var c = new SKCanvas(src)) c.Clear(SKColors.White);
        src.SetPixel(5, 3, SKColors.Red); // marker
        using var dst = PageOrientation.Rotate(src, turns);
        var q = new[] { new Pt(5.5, 3.5), new Pt(6.5, 3.5), new Pt(6.5, 4.5), new Pt(5.5, 4.5) };
        var m = PageOrientation.MapQuad(q, turns, src.Width, src.Height);
        // the mapped point (any corner) must be next to the red pixel in the turned bitmap
        int hits = 0;
        foreach (var p in m)
            for (int dx = -1; dx <= 0; dx++) for (int dy = -1; dy <= 0; dy++)
            {
                int x = (int)Math.Round(p.X) + dx, y = (int)Math.Round(p.Y) + dy;
                if (x >= 0 && y >= 0 && x < dst.Width && y < dst.Height && dst.GetPixel(x, y) == SKColors.Red) hits++;
            }
        Assert.True(hits > 0);
        Assert.Equal(turns == 2 ? (40, 20) : (20, 40), (dst.Width, dst.Height));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MapQuad_returns_corners_in_top_left_top_right_bottom_right_bottom_left_order(int turns)
    {
        var wide = new[] { new Pt(10, 10), new Pt(110, 10), new Pt(110, 30), new Pt(10, 30) };
        var m = PageOrientation.MapQuad(wide, turns, 200, 100);
        double x0 = m.Min(p => p.X), x1 = m.Max(p => p.X), y0 = m.Min(p => p.Y), y1 = m.Max(p => p.Y);
        Assert.Equal((x0, y0), (m[0].X, m[0].Y));
        Assert.Equal((x1, y0), (m[1].X, m[1].Y));
        Assert.Equal((x1, y1), (m[2].X, m[2].Y));
        Assert.Equal((x0, y1), (m[3].X, m[3].Y));
        if (turns != 2) { var (w, h) = PageOrientation.Sides(m); Assert.True(h > w); } // a quarter turn lays the line on its side
    }

    [Fact]
    public void TallFraction_counts_boxes_taller_than_one_and_a_half_times_their_width()
    {
        var wide = new[] { new Pt(0, 0), new Pt(100, 0), new Pt(100, 20), new Pt(0, 20) };
        var tall = new[] { new Pt(0, 0), new Pt(20, 0), new Pt(20, 100), new Pt(0, 100) };
        Assert.Equal(0.5, PageOrientation.TallFraction(new[] { wide, tall }), 3);
        Assert.Equal(0, PageOrientation.TallFraction(Array.Empty<Pt[]>()));
    }
}

public class DocumentReaderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "indicocr_doc_" + Guid.NewGuid().ToString("N"));
    public DocumentReaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData(595f, 842f, 300)]      // A4 renders at the full 300 dpi (3508 px)
    [InlineData(2384f, 3370f, 170)]    // A0: lowered so the long side stays within 8000 px
    [InlineData(14400f, 14400f, 40)]
    public void Pdf_render_dpi_keeps_the_long_side_within_the_cap(float wPt, float hPt, int expected)
    {
        int dpi = DocumentReader.PdfRenderDpi(wPt, hPt);
        Assert.Equal(expected, dpi);
        Assert.True(Math.Max(wPt, hPt) * dpi / 72.0 <= DocumentReader.MaxPdfSidePixels + 1);
    }

    [Fact]
    public void An_A0_pdf_page_is_rendered_within_the_pixel_cap_and_reports_its_dpi()
    {
        var path = Path.Combine(_dir, "a0.pdf");
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            var p = doc.AddPage();
            p.Width = PdfSharp.Drawing.XUnit.FromPoint(2384);
            p.Height = PdfSharp.Drawing.XUnit.FromPoint(3370);
            doc.Save(path);
        }
        using var e = DocumentReader.ReadPages(path).GetEnumerator();
        Assert.True(e.MoveNext());
        using var bmp = e.Current.Bitmap;
        Assert.InRange(Math.Max(bmp.Width, bmp.Height), 7000, DocumentReader.MaxPdfSidePixels + 1);
        Assert.Equal(170, e.Current.Dpi);
    }

    static uint Crc(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in d) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1; }
        return ~c;
    }

    static byte[] PngHeaderOnly(int w, int h, uint? ppm = null)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var len = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length); ms.Write(len);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(td);
            var crc = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(td)); ms.Write(crc);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)h);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk("IHDR", ihdr);
        if (ppm != null)
        {
            var phys = new byte[9];
            BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(0, 4), ppm.Value);
            BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4, 4), ppm.Value);
            phys[8] = 1;
            Chunk("pHYs", phys);
        }
        Chunk("IDAT", new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 });
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    [Fact]
    public void A_20000_by_20000_png_is_refused_before_any_pixel_decode()
    {
        var path = Path.Combine(_dir, "huge.png");
        File.WriteAllBytes(path, PngHeaderOnly(20000, 20000));
        var ex = Assert.Throws<InvalidDataException>(() => DocumentReader.DecodeWithOrientation(path));
        Assert.Contains("too large", ex.Message);
        Assert.Contains("20000x20000", ex.Message);
    }

    [Fact]
    public void Png_dpi_comes_from_the_phys_chunk()
    {
        Assert.Equal(300, ImageDpi.Parse(PngHeaderOnly(10, 10, 11811)));   // 300 dpi = 11811 px/m
        Assert.Equal(200, ImageDpi.Parse(PngHeaderOnly(10, 10, 7874)));
        Assert.Null(ImageDpi.Parse(PngHeaderOnly(10, 10, 2835)));          // 72 dpi: placeholder, treated as unknown
        Assert.Null(ImageDpi.Parse(PngHeaderOnly(10, 10)));
    }

    [Fact]
    public void Jpeg_dpi_comes_from_jfif()
    {
        // SOI, APP0 JFIF v1.01, units=1 (dpi), 150x150, no thumbnail, then EOI
        var jfif = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 1, 0, 150, 0, 150, 0, 0, 0xFF, 0xD9 };
        Assert.Equal(150, ImageDpi.Parse(jfif));
        jfif[13] = 0; // units 0: aspect ratio only
        Assert.Null(ImageDpi.Parse(jfif));
    }

    [Fact]
    public void Tiff_dpi_is_read_from_the_resolution_tags_and_pixels_copied_through()
    {
        var path = Path.Combine(_dir, "t.tif");
        using (var t = Tiff.Open(path, "w"))
        {
            t.SetField(TiffTag.IMAGEWIDTH, 8); t.SetField(TiffTag.IMAGELENGTH, 4);
            t.SetField(TiffTag.SAMPLESPERPIXEL, 1); t.SetField(TiffTag.BITSPERSAMPLE, 8);
            t.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK); t.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            t.SetField(TiffTag.ROWSPERSTRIP, 4);
            t.SetField(TiffTag.XRESOLUTION, 200.0); t.SetField(TiffTag.YRESOLUTION, 200.0); t.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH);
            for (int y = 0; y < 4; y++) t.WriteScanline(Enumerable.Repeat((byte)(y * 60), 8).ToArray(), y);
        }
        using var e = DocumentReader.ReadPages(path).GetEnumerator();
        Assert.True(e.MoveNext());
        using var bmp = e.Current.Bitmap;
        Assert.Equal(200, e.Current.Dpi);
        Assert.Equal((8, 4), (bmp.Width, bmp.Height));
        Assert.Equal(60, bmp.GetPixel(3, 1).Red);
        Assert.Equal(180, bmp.GetPixel(0, 3).Green);
    }

    [Fact]
    public void Normal_images_decode_and_pass_exif_orientation_through()
    {
        using var src = new SKBitmap(30, 10);
        using (var c = new SKCanvas(src)) c.Clear(SKColors.White);
        var path = Path.Combine(_dir, "a.png");
        using (var img = SKImage.FromBitmap(src)) using (var d = img.Encode(SKEncodedImageFormat.Png, 100)) File.WriteAllBytes(path, d.ToArray());
        using var bmp = DocumentReader.DecodeWithOrientation(path);
        Assert.Equal((30, 10), (bmp.Width, bmp.Height));
    }
}

public class PdfPageSizeTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "indicocr_pps_" + Guid.NewGuid().ToString("N"));
    public PdfPageSizeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static OcrPage Page(int w, int h, int? dpi) => new(0, w, h, Array.Empty<OcrLine>(), TimeSpan.Zero) { SourceDpi = dpi };

    static (double W, double H) MediaBox(string pdf)
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(pdf, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        return (doc.Pages[0].Width.Point, doc.Pages[0].Height.Point);
    }

    [Fact]
    public void Page_size_follows_the_source_dpi_and_defaults_to_300()
    {
        using var bmp = new SKBitmap(1240, 1754);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.White);
        var p150 = Path.Combine(_dir, "150.pdf");
        SearchablePdfExporter.Write(new[] { (bmp, Page(1240, 1754, 150)) }, p150);
        var (w, h) = MediaBox(p150);
        Assert.Equal(595.2, w, 0); Assert.Equal(841.9, h, 0); // an A4 scan at 150 dpi stays A4

        var pDef = Path.Combine(_dir, "def.pdf");
        SearchablePdfExporter.Write(new[] { (bmp, Page(1240, 1754, null)) }, pDef);
        (w, h) = MediaBox(pDef);
        Assert.Equal(1240 * 72.0 / 300, w, 0);
    }

    [Fact]
    public void Preencoded_jpeg_bytes_are_embedded_unchanged()
    {
        using var bmp = new SKBitmap(200, 100);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.Gray);
        byte[] jpeg;
        using (var img = SKImage.FromBitmap(bmp)) using (var d = img.Encode(SKEncodedImageFormat.Jpeg, 60)) jpeg = d.ToArray();
        var path = Path.Combine(_dir, "j.pdf");
        SearchablePdfExporter.Write(new[] { (jpeg, Page(200, 100, 200)) }, path);
        var raw = File.ReadAllBytes(path);
        Assert.True(Contains(raw, jpeg), "the exact JPEG bytes should be in the PDF (DCTDecode stream)");
        Assert.Equal(72.0, MediaBox(path).W, 0);
    }

    static bool Contains(byte[] hay, byte[] needle) => hay.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public void Points_per_pixel_respects_the_pdf_page_limit()
    {
        Assert.Equal(72.0 / 300, SearchablePdfExporter.PointsPerPixel(2480, 3508, null), 6);
        Assert.True(60000 * SearchablePdfExporter.PointsPerPixel(60000, 100, 100) <= 14400.0001);
    }
}

public class EngineLoadFailureTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "indicocr_lf_" + Guid.NewGuid().ToString("N"));
    public EngineLoadFailureTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Kannada_is_reported_as_beta_and_the_others_are_not()
    {
        Assert.True(OcrEngine.IsBeta(OcrLanguage.Kannada));
        foreach (var l in new[] { OcrLanguage.English, OcrLanguage.Telugu, OcrLanguage.Hindi, OcrLanguage.Tamil })
            Assert.False(OcrEngine.IsBeta(l));
    }

    [Fact]
    public void A_failed_recognizer_load_is_not_cached()
    {
        File.WriteAllText(Path.Combine(_dir, "det.onnx"), "x");
        using var eng = new OcrEngine(_dir);
        using var bmp = new SKBitmap(200, 60);
        var box = new RectangleF(0, 0, 200, 60);
        Assert.Throws<FileNotFoundException>(() => eng.RecognizeLine(bmp, box, new[] { OcrLanguage.Telugu }));      // files missing
        File.WriteAllText(Path.Combine(_dir, "rec_te.onnx"), "partial copy");
        File.WriteAllText(Path.Combine(_dir, "dict_te.txt"), "a\nb\n");
        var second = Record.Exception(() => eng.RecognizeLine(bmp, box, new[] { OcrLanguage.Telugu }));              // damaged model
        Assert.NotNull(second);
        Assert.IsNotType<FileNotFoundException>(second);                                                              // it really tried again
    }

    [ModelsOnlyFact]
    public void A_recognizer_that_failed_to_load_works_after_the_files_are_fixed()
    {
        var models = TestEnv.ModelsDir!;
        File.Copy(Path.Combine(models, "det.onnx"), Path.Combine(_dir, "det.onnx"));
        File.Copy(Path.Combine(models, "dict_te.txt"), Path.Combine(_dir, "dict_te.txt"));
        File.WriteAllText(Path.Combine(_dir, "rec_te.onnx"), "partial copy");
        using var eng = new OcrEngine(_dir);
        using var bmp = new SKBitmap(400, 80);
        using (var c = new SKCanvas(bmp)) c.Clear(SKColors.White);
        var box = new RectangleF(0, 0, 400, 80);
        Assert.ThrowsAny<Exception>(() => eng.RecognizeLine(bmp, box, new[] { OcrLanguage.Telugu }));
        File.Copy(Path.Combine(models, "rec_te.onnx"), Path.Combine(_dir, "rec_te.onnx"), overwrite: true);
        var line = eng.RecognizeLine(bmp, box, new[] { OcrLanguage.Telugu });
        Assert.NotNull(line);
    }
}
