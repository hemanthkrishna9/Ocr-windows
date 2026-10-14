using System.Drawing;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Xunit;
using Xunit.Abstractions;

namespace IndicOcr.Core.Tests;

/// <summary>Model-backed tests for recognizer selection, page orientation, column order and the identifier fix.</summary>
public class IntegrationBehaviorTests
{
    readonly ITestOutputHelper _out;
    public IntegrationBehaviorTests(ITestOutputHelper o) => _out = o;

    static readonly string[] TeluguLines =
    {
        "తెలుగు భాష చాలా మధురమైనది",
        "భారతదేశం నా మాతృభూమి",
        "ఆంధ్ర ప్రదేశ్ ప్రభుత్వం",
        "ప్రతి పౌరుడు తన హక్కులను తెలుసుకోవాలి",
    };

    static bool HasScript(string s, int from, int to) => s.Any(c => c >= from && c <= to);

    // ---- item 2: recognizer selection must not let a recognizer that drops a script win ------------------------

    [ModelsFact]
    public void Kannada_plus_English_keeps_the_kannada_word_even_when_both_recognizers_run()
    {
        var lines = new[] { ("ದಿನಾಂಕ 12-05-2023 Rs.5000", "NotoSansKannada-Regular.ttf"), ("ಕರ್ನಾಟಕ ಸರ್ಕಾರ Government of Karnataka", "NotoSansKannada-Regular.ttf") };
        using var bmp = TestEnv.RenderMixed(lines, 2200, 330, 46);
        using var eng = new OcrEngine(TestEnv.ModelsDir!, new OcrEngineOptions { EnglishViaIndicRecognizer = false });
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Kannada, OcrLanguage.English });
        Assert.Equal(2, page.Lines.Count);
        foreach (var l in page.Lines) _out.WriteLine($"[{l.Language} {l.Confidence:F2}] {l.Text}");
        Assert.All(page.Lines, l => Assert.True(HasScript(l.Text, 0x0C80, 0x0CFF), "Kannada text was dropped: " + l.Text));
        Assert.Contains("12-05-2023", page.Lines[0].Text);
        Assert.Contains("Government of Karnataka", page.Lines[1].Text);
    }

    [ModelsFact]
    public void Kannada_plus_English_uses_only_the_kannada_recognizer_by_default()
    {
        var lines = new[] { ("ಕರ್ನಾಟಕ ಸರ್ಕಾರ Government of Karnataka", "NotoSansKannada-Regular.ttf"), ("Registration of sale deed number 4521", "NotoSans-Regular.ttf") };
        using var bmp = TestEnv.RenderMixed(lines, 2200, 260, 46);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Kannada, OcrLanguage.English });
        Assert.Equal(2, page.Lines.Count);
        Assert.Equal(OcrLanguage.Kannada, page.Lines[0].Language);
        Assert.Equal(OcrLanguage.English, page.Lines[1].Language); // Latin-only result is relabelled English
        Assert.True(TestEnv.Cer("Registration of sale deed number 4521", page.Lines[1].Text) < 0.15, page.Lines[1].Text);
    }

    [ModelsFact]
    public void Telugu_Hindi_English_all_three_recognizers_keep_each_lines_own_script()
    {
        var rows = new (string Text, string Font)[]
        {
            ("రిజిస్ట్రేషన్ నంబర్ 4521/2023", "NotoSansTelugu-Regular.ttf"),
            ("मंडल: Kukatpally, जिला: Medchal", "NotoSansDevanagari-Regular.ttf"),
            ("PAN ABCDE1234F కలిగిన వ్యక్తి", "NotoSansTelugu-Regular.ttf"),
            ("Survey No. 123/4A, Extent 2.35 Acres", "NotoSans-Regular.ttf"),
        };
        using var bmp = TestEnv.RenderMixed(rows, 2200, 130 * rows.Length, 46);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Telugu, OcrLanguage.Hindi, OcrLanguage.English });
        foreach (var l in page.Lines) _out.WriteLine($"[{l.Language} {l.Confidence:F2}] {l.Text}");
        Assert.Equal(rows.Length, page.Lines.Count);
        Assert.True(HasScript(page.Lines[0].Text, 0x0C00, 0x0C7F));
        Assert.Equal(OcrLanguage.Telugu, page.Lines[0].Language);
        Assert.True(HasScript(page.Lines[1].Text, 0x0900, 0x097F));
        Assert.Equal(OcrLanguage.Hindi, page.Lines[1].Language);
        Assert.True(HasScript(page.Lines[2].Text, 0x0C00, 0x0C7F), "Telugu part of a mixed PAN line was dropped: " + page.Lines[2].Text);
        Assert.Contains("ABCDE1234F", page.Lines[2].Text);
        Assert.Equal(OcrLanguage.English, page.Lines[3].Language);
        Assert.True(TestEnv.Cer(rows[3].Text, page.Lines[3].Text) < 0.15, page.Lines[3].Text);
    }

    [ModelsFact]
    public void A_Latin_only_result_is_labelled_English_even_when_every_recognizer_runs()
    {
        using var bmp = TestEnv.Render(new[] { ("Registration of sale deed number 4521", "NotoSans-Regular.ttf") }, 1400, 140, 44);
        using var eng = new OcrEngine(TestEnv.ModelsDir!, new OcrEngineOptions { EnglishViaIndicRecognizer = false });
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Telugu, OcrLanguage.Hindi, OcrLanguage.English });
        Assert.Single(page.Lines);
        Assert.Equal(OcrLanguage.English, page.Lines[0].Language);
    }

    // ---- item 14: identifiers inside mixed lines ------------------------------------------------------------------

    [ModelsFact]
    public void Survey_numbers_in_telugu_lines_keep_their_latin_letters()
    {
        var rows = new[]
        {
            ("రిజిస్ట్రేషన్ నంబర్ 4521/2023 Sy.No.12/A", "NotoSansTelugu-Regular.ttf"),
            ("తేదీ 12-05-2023 Rs.5000 Sy.No.340/2B", "NotoSansTelugu-Regular.ttf"),
        };
        using var bmp = TestEnv.RenderMixed(rows, 2200, 260, 46);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Telugu, OcrLanguage.English });
        foreach (var l in page.Lines) _out.WriteLine(l.Text);
        Assert.Equal(2, page.Lines.Count);
        Assert.Contains("Sy.No.12/A", page.Lines[0].Text);
        Assert.Contains("Sy.No.340/2B", page.Lines[1].Text);
    }

    // ---- item 4: orientation ----------------------------------------------------------------------------------------

    static SKBitmap TurnClockwise(SKBitmap src, int degrees)
    {
        bool swap = degrees % 180 != 0;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height);
        using var c = new SKCanvas(dst);
        c.Clear(SKColors.White);
        c.Translate(dst.Width / 2f, dst.Height / 2f);
        c.RotateDegrees(degrees);
        c.Translate(-src.Width / 2f, -src.Height / 2f);
        using var im = SKImage.FromBitmap(src);
        c.DrawImage(im, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        return dst;
    }

    static SKBitmap UprightTeluguPage() =>
        TestEnv.Render(Enumerable.Range(0, 8).Select(i => (TeluguLines[i % 4] + " " + TeluguLines[(i + 1) % 4], "NotoSansTelugu-Regular.ttf")).ToList(), 2000, 900, 44);

    [ModelsFact]
    public void An_upright_page_is_not_rotated()
    {
        using var bmp = UprightTeluguPage();
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Telugu });
        Assert.Equal(0, page.RotationApplied);
        Assert.Equal((bmp.Width, bmp.Height), (page.Width, page.Height));
    }

    [ModelsTheory]
    [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void A_page_turned_by_a_multiple_of_90_degrees_is_read_upright(int turnedClockwise)
    {
        using var up = UprightTeluguPage();
        using var turned = TurnClockwise(up, turnedClockwise);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var (page, rotated) = eng.RecognizeOriented(turned, new[] { OcrLanguage.Telugu });
        using var _ = rotated;
        Assert.Equal((360 - turnedClockwise) % 360, page.RotationApplied);
        Assert.NotNull(rotated);
        Assert.Equal((rotated!.Width, rotated.Height), (page.Width, page.Height));
        Assert.Equal((up.Width, up.Height), (page.Width, page.Height));
        Assert.Equal(8, page.Lines.Count);
        double cer = 0;
        for (int i = 0; i < 8; i++) cer += TestEnv.Cer(TeluguLines[i % 4] + " " + TeluguLines[(i + 1) % 4], page.Lines[i].Text);
        _out.WriteLine($"turned {turnedClockwise}: mean CER {cer / 8:F3}");
        Assert.True(cer / 8 < 0.08, $"mean CER {cer / 8:F3}");
        foreach (var l in page.Lines)
        {
            Assert.InRange(l.Box.Left, -1, page.Width + 1);
            Assert.InRange(l.Box.Bottom, 0, page.Height + 1);
            Assert.True(l.Box.Width > l.Box.Height, "boxes are in the upright frame");
        }
    }

    [ModelsFact]
    public void Rotation_can_be_switched_off()
    {
        using var up = UprightTeluguPage();
        using var turned = TurnClockwise(up, 180);
        using var eng = new OcrEngine(TestEnv.ModelsDir!, new OcrEngineOptions { AutoRotate = false });
        var (page, rotated) = eng.RecognizeOriented(turned, new[] { OcrLanguage.Telugu });
        Assert.Null(rotated);
        Assert.Equal(0, page.RotationApplied);
    }

    [ModelsFact]
    public async Task RecognizeFileAsync_returns_the_turned_bitmap_so_boxes_match_it()
    {
        using var up = UprightTeluguPage();
        using var turned = TurnClockwise(up, 90);
        var path = Path.Combine(Path.GetTempPath(), "indicocr_rot_" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var img = SKImage.FromBitmap(turned)) using (var d = img.Encode(SKEncodedImageFormat.Png, 100)) File.WriteAllBytes(path, d.ToArray());
            using var eng = new OcrEngine(TestEnv.ModelsDir!);
            int n = 0;
            await foreach (var (image, page) in eng.RecognizeFileAsync(path, new[] { OcrLanguage.Telugu }))
            {
                using (image)
                {
                    n++;
                    Assert.Equal(270, page.RotationApplied);
                    Assert.Equal((up.Width, up.Height), (image.Width, image.Height));
                    Assert.Equal((image.Width, image.Height), (page.Width, page.Height));
                    Assert.All(page.Lines, l => Assert.True(l.Box.Right <= image.Width + 1 && l.Box.Bottom <= image.Height + 1));
                }
            }
            Assert.Equal(1, n);
        }
        finally { File.Delete(path); }
    }

    // ---- item 9: columns ------------------------------------------------------------------------------------------------

    [ModelsFact]
    public void A_two_column_page_is_read_column_by_column()
    {
        var left = new[] { TeluguLines[0], TeluguLines[1], TeluguLines[2], TeluguLines[3], "భారతదేశం నా మాతృభూమి ప్రతి", "తెలుగు భాష" };
        var right = new[] { "Registration of sale deed", "Survey number 4521 Hyderabad", "Government of Andhra Pradesh", "Total extent two acres", "Date of registration 2023", "Village Kukatpally" };
        using var bmp = new SKBitmap(2400, 700);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.White);
            using var te = new SKFont(SKTypeface.FromFile(TestEnv.Font("NotoSansTelugu-Regular.ttf")), 44);
            using var en = new SKFont(SKTypeface.FromFile(TestEnv.Font("NotoSans-Regular.ttf")), 44);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            for (int i = 0; i < 6; i++)
            {
                c.DrawShapedText(left[i], 80, 80 + i * 105, SKTextAlign.Left, te, paint);
                c.DrawShapedText(right[i], 1300, 80 + i * 105, SKTextAlign.Left, en, paint);
            }
        }
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Telugu, OcrLanguage.English });
        foreach (var l in page.Lines) _out.WriteLine($"{l.Box.Left:F0},{l.Box.Top:F0} {l.Text}");
        Assert.Equal(12, page.Lines.Count);
        Assert.All(page.Lines.Take(6), l => Assert.True(l.Box.Left < 600, "first six lines are the left column"));
        Assert.All(page.Lines.Skip(6), l => Assert.True(l.Box.Left > 1000, "last six lines are the right column"));
        Assert.True(page.Lines.Take(6).Zip(page.Lines.Take(6).Skip(1)).All(p => p.First.Box.Top < p.Second.Box.Top));
    }

    // ---- item 7: concurrency -----------------------------------------------------------------------------------------------

    [ModelsFact]
    public void Concurrent_recognition_on_one_engine_gives_identical_results()
    {
        using var bmp = TestEnv.Render(TeluguLines.Select(t => (t, "NotoSansTelugu-Regular.ttf")).ToList(), 1400, 520, 44);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var reference = eng.Recognize(bmp, new[] { OcrLanguage.Telugu }).Text;
        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() => eng.Recognize(bmp, new[] { OcrLanguage.Telugu }).Text)).ToArray();
        Task.WaitAll(tasks);
        Assert.All(tasks, t => Assert.Equal(reference, t.Result));
    }
}

/// <summary>Theory variant of <see cref="ModelsFactAttribute"/>.</summary>
public sealed class ModelsTheoryAttribute : TheoryAttribute
{
    public ModelsTheoryAttribute()
    {
        if (TestEnv.ModelsDir == null) Skip = "INDICOCR_MODELS not set (or folder missing); integration test skipped.";
        else if (TestEnv.FontsDir == null) Skip = "Noto fonts not found (set INDICOCR_TEST_FONTS).";
    }
}
