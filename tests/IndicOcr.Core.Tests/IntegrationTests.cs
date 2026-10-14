using System.Diagnostics;
using System.Drawing;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace IndicOcr.Core.Tests;

public class IntegrationTests
{
    readonly ITestOutputHelper _out;
    public IntegrationTests(ITestOutputHelper o) => _out = o;

    static readonly string[] TeluguLines =
    {
        "తెలుగు భాష చాలా మధురమైనది",
        "భారతదేశం నా మాతృభూమి",
        "ఆంధ్ర ప్రదేశ్ ప్రభుత్వం",
        "ప్రతి పౌరుడు తన హక్కులను తెలుసుకోవాలి",
    };

    [ModelsFact]
    public void Available_languages_lists_all_five_models()
    {
        var l = OcrEngine.AvailableLanguages(TestEnv.ModelsDir!);
        Assert.Equal(5, l.Count);
    }

    [ModelsFact]
    public void Every_recognizer_matches_its_dictionary_class_count()
    {
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        using var blank = new SKBitmap(400, 80);
        using (var c = new SKCanvas(blank)) c.Clear(SKColors.White);
        foreach (var lang in OcrEngine.AvailableLanguages(TestEnv.ModelsDir!))
        {
            var line = eng.RecognizeLine(blank, new RectangleF(0, 0, 400, 80), new[] { lang }); // throws on class mismatch
            Assert.NotNull(line);
        }
    }

    [ModelsFact]
    public void Telugu_lines_are_recognised_with_low_character_error_rate()
    {
        using var bmp = TestEnv.Render(TeluguLines.Select(t => (t, "NotoSansTelugu-Regular.ttf")).ToList(), 1400, 520, 44);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Telugu });
        _out.WriteLine($"lines={page.Lines.Count} elapsed={page.Elapsed.TotalSeconds:F2}s");
        Assert.Equal(TeluguLines.Length, page.Lines.Count);
        double total = 0;
        for (int i = 0; i < TeluguLines.Length; i++)
        {
            double cer = TestEnv.Cer(TeluguLines[i], page.Lines[i].Text);
            _out.WriteLine($"CER {cer:F3} conf {page.Lines[i].Confidence:F2}");
            total += cer;
            Assert.Equal(OcrLanguage.Telugu, page.Lines[i].Language);
            Assert.True(page.Lines[i].Box.Width > 100);
        }
        Assert.True(total / TeluguLines.Length < 0.05, $"mean CER {total / TeluguLines.Length:F3}");
    }

    static readonly string[] HindiLines =
    {
        "भारत मेरा देश है",
        "हिंदी भारत की राजभाषा है",
        "सत्यमेव जयते",
        "प्रत्येक नागरिक को अपने अधिकार जानने चाहिए",
    };

    static readonly string[] TamilLines =
    {
        "தமிழ் மொழி இனிமையானது",
        "இந்தியா என் தாய்நாடு",
        "தமிழ்நாடு அரசு",
        "ஒவ்வொரு குடிமகனும் தன் உரிமைகளை அறிய வேண்டும்",
    };

    static readonly string[] KannadaLines =
    {
        "ಕನ್ನಡ ಭಾಷೆ ಸುಂದರವಾಗಿದೆ",
        "ಭಾರತ ನನ್ನ ತಾಯ್ನಾಡು",
        "ಕರ್ನಾಟಕ ಸರ್ಕಾರ",
    };

    double MeanCer(OcrLanguage lang, string[] lines, string font, string label)
    {
        using var bmp = TestEnv.Render(lines.Select(t => (t, font)).ToList(), 1400, 130 * lines.Length, 44);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { lang });
        Assert.Equal(lines.Length, page.Lines.Count);
        double total = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            double cer = TestEnv.Cer(lines[i], page.Lines[i].Text);
            _out.WriteLine($"{label} CER {cer:F3}: {page.Lines[i].Text}");
            total += cer;
        }
        return total / lines.Length;
    }

    [ModelsFact]
    public void Hindi_lines_are_recognised_with_low_character_error_rate()
    {
        double m = MeanCer(OcrLanguage.Hindi, HindiLines, "NotoSansDevanagari-Regular.ttf", "hi");
        Assert.True(m < 0.05, $"mean CER {m:F3}");
    }

    [ModelsFact]
    public void Tamil_lines_are_recognised_with_low_character_error_rate()
    {
        double m = MeanCer(OcrLanguage.Tamil, TamilLines, "NotoSansTamil-Regular.ttf", "ta");
        Assert.True(m < 0.05, $"mean CER {m:F3}");
    }

    /// <summary>
    /// BETA: Kannada still uses the older PP-OCRv3 recognizer (no PP-OCRv5 Kannada model is available), so the bar is far looser than for
    /// the other languages (see OcrEngine.IsBeta). Measured mean CER 0.341 on these three lines; the bound is that plus a small margin,
    /// so a real regression fails the test. Tighten it when a better Kannada model is adopted.
    /// </summary>
    [ModelsFact]
    public void Kannada_lines_are_recognised_with_looser_bound_PP_OCRv3()
    {
        Assert.True(OcrEngine.IsBeta(OcrLanguage.Kannada));
        double m = MeanCer(OcrLanguage.Kannada, KannadaLines, "NotoSansKannada-Regular.ttf", "kn");
        Assert.True(m < 0.40, $"mean CER {m:F3}"); // measured 0.341
    }

    static readonly string[] MixedTeluguEnglish =
    {
        "తెలుగు భాష చాలా మధురమైనది 2024 Hyderabad",
        "Sale deed number 4521 తేదీ 12-05-2023",
        "ఆంధ్ర ప్రదేశ్ ప్రభుత్వం Government of AP",
    };

    [ModelsFact]
    public void Mixed_telugu_and_english_lines_are_read_by_the_telugu_recognizer_alone()
    {
        using var bmp = TestEnv.RenderMixed(MixedTeluguEnglish.Select(t => (t, "NotoSansTelugu-Regular.ttf")).ToList(), 1500, 130 * 3, 44);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Telugu, OcrLanguage.English });
        Assert.Equal(MixedTeluguEnglish.Length, page.Lines.Count);
        double total = 0;
        for (int i = 0; i < MixedTeluguEnglish.Length; i++)
        {
            double cer = TestEnv.Cer(MixedTeluguEnglish[i], page.Lines[i].Text);
            _out.WriteLine($"mixed CER {cer:F3}: {page.Lines[i].Text}");
            total += cer;
        }
        Assert.True(total / MixedTeluguEnglish.Length < 0.08, $"mean CER {total / MixedTeluguEnglish.Length:F3}");
    }

    [ModelsFact]
    public void English_line_is_recognised()
    {
        const string text = "The quick brown fox jumps over 13 lazy dogs";
        using var bmp = TestEnv.Render(new[] { (text, "NotoSans-Regular.ttf") }, 1400, 140, 44);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { OcrLanguage.English });
        Assert.Single(page.Lines);
        double cer = TestEnv.Cer(text, page.Lines[0].Text);
        _out.WriteLine($"CER {cer:F3}: {page.Lines[0].Text}");
        Assert.True(cer < 0.1, $"CER {cer:F3}");
    }

    [ModelsFact]
    public void Router_picks_the_right_language_for_mixed_page()
    {
        var lines = new[]
        {
            (TeluguLines[0], "NotoSansTelugu-Regular.ttf"),
            ("Registration of sale deed number 4521", "NotoSans-Regular.ttf"),
        };
        using var bmp = TestEnv.Render(lines, 1400, 260, 44);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Telugu, OcrLanguage.English });
        Assert.Equal(2, page.Lines.Count);
        Assert.Equal(OcrLanguage.Telugu, page.Lines[0].Language);
        Assert.Equal(OcrLanguage.English, page.Lines[1].Language);
        Assert.True(TestEnv.Cer(TeluguLines[0], page.Lines[0].Text) < 0.05);
        Assert.True(TestEnv.Cer("Registration of sale deed number 4521", page.Lines[1].Text) < 0.1);
    }

    [ModelsFact]
    public void Both_recognizers_run_when_the_english_shortcut_is_off()
    {
        var lines = new[]
        {
            (TeluguLines[0], "NotoSansTelugu-Regular.ttf"),
            ("Registration of sale deed number 4521", "NotoSans-Regular.ttf"),
        };
        using var bmp = TestEnv.Render(lines, 1400, 260, 44);
        using var eng = new OcrEngine(TestEnv.ModelsDir!, new OcrEngineOptions { EnglishViaIndicRecognizer = false });
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Telugu, OcrLanguage.English });
        Assert.Equal(2, page.Lines.Count);
        Assert.Equal(OcrLanguage.Telugu, page.Lines[0].Language);
        Assert.Equal(OcrLanguage.English, page.Lines[1].Language);
    }

    [ModelsFact]
    public void RecognizeLine_rereads_one_region()
    {
        using var bmp = TestEnv.Render(TeluguLines.Select(t => (t, "NotoSansTelugu-Regular.ttf")).ToList(), 1400, 520, 44);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Telugu });
        var box = page.Lines[1].Box;
        var again = eng.RecognizeLine(bmp, box, new[] { OcrLanguage.Telugu });
        Assert.True(TestEnv.Cer(TeluguLines[1], again.Text) < 0.05, again.Text);
        Assert.Equal(box, again.Box);
    }

    [ModelsFact]
    public async Task RecognizeFileAsync_reads_png_and_pdf_roundtrip()
    {
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var dir = Path.Combine(Path.GetTempPath(), "indicocr_it_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var bmp = TestEnv.Render(TeluguLines.Take(2).Select(t => (t, "NotoSansTelugu-Regular.ttf")).ToList(), 1400, 260, 44);
            var png = Path.Combine(dir, "a.png");
            using (var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100))
            using (var fs = File.Create(png)) data.SaveTo(fs);

            int n = 0;
            await foreach (var (img, page) in eng.RecognizeFileAsync(png, new[] { OcrLanguage.Telugu }))
            {
                using (img) { n++; Assert.Equal(2, page.Lines.Count); }
            }
            Assert.Equal(1, n);

            // text -> searchable PDF -> OCR the PDF (PDF is rendered at 300 dpi by PDFium)
            var again = eng.Recognize(bmp, new[] { OcrLanguage.Telugu });
            var pdf = Path.Combine(dir, "a.pdf");
            SearchablePdfExporter.Write(new[] { (bmp, again) }, pdf, TestEnv.FontsDir);
            int pages = 0;
            await foreach (var (img, page) in eng.RecognizeFileAsync(pdf, new[] { OcrLanguage.Telugu }))
            {
                using (img)
                {
                    pages++;
                    Assert.InRange(img.Width, 1390, 1410);
                    Assert.True(page.Lines.Count >= 2);
                }
            }
            Assert.Equal(1, pages);
        }
        finally { Directory.Delete(dir, true); }
    }

    [ModelsFact]
    public void A4_page_timing_and_accuracy()
    {
        const int W = 2480, H = 3508;
        var rnd = new Random(7);
        var lines = new List<(string, string)>();
        for (int i = 0; i < 34; i++) lines.Add((TeluguLines[rnd.Next(TeluguLines.Length)] + " " + TeluguLines[rnd.Next(TeluguLines.Length)], "NotoSansTelugu-Regular.ttf"));
        using var bmp = TestEnv.Render(lines, W, H, 46, 1.9f, 150);
        var dump = Environment.GetEnvironmentVariable("INDICOCR_DUMP_PAGE");
        if (!string.IsNullOrEmpty(dump))
        {
            using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
            using var fs = File.Create(dump);
            data.SaveTo(fs);
        }
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var sw = Stopwatch.StartNew();
        var cold = eng.Recognize(bmp, new[] { OcrLanguage.Telugu });
        double coldS = sw.Elapsed.TotalSeconds;
        var warm = new List<double>();
        OcrPage last = cold;
        for (int i = 0; i < 3; i++) { last = eng.Recognize(bmp, new[] { OcrLanguage.Telugu }); warm.Add(last.Elapsed.TotalSeconds); }
        double cer = 0;
        for (int i = 0; i < Math.Min(lines.Count, last.Lines.Count); i++) cer += TestEnv.Cer(lines[i].Item1, last.Lines[i].Text);
        _out.WriteLine($"A4 {W}x{H} threads={Environment.ProcessorCount} cold={coldS:F2}s warm={string.Join(",", warm.Select(w => w.ToString("F2")))}s lines={last.Lines.Count}/{lines.Count} meanCER={cer / lines.Count:F3}");
        // Loose timing guard (measured about 2 s on 4 cores): only a catastrophic regression, or a missing speed rule, trips it.
        Assert.True(coldS < 15 && warm.Max() < 15, $"A4 page too slow: cold {coldS:F1}s, warm max {warm.Max():F1}s");
        Assert.True(last.Lines.Count >= lines.Count - 2);
        Assert.True(cer / lines.Count < 0.05, $"mean CER {cer / lines.Count:F3}");
    }
}
