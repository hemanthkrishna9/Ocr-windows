using System.Diagnostics;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace IndicOcr.Core.Tests;

/// <summary>Automatic script detection (OcrLanguage.Auto): same text as explicit mode, correct detected languages, bounded cost.</summary>
[Collection("Timing")]
public class AutoLanguageTests
{
    readonly ITestOutputHelper _out;
    public AutoLanguageTests(ITestOutputHelper o) => _out = o;

    internal const string TeFont = "NotoSansTelugu-Regular.ttf", HiFont = "NotoSansDevanagari-Regular.ttf",
        TaFont = "NotoSansTamil-Regular.ttf", KnFont = "NotoSansKannada-Regular.ttf", EnFont = "NotoSans-Regular.ttf";

    internal static readonly string[] Te =
    {
        "తెలుగు భాష చాలా మధురమైనది", "భారతదేశం నా మాతృభూమి", "ఆంధ్ర ప్రదేశ్ ప్రభుత్వం",
        "ప్రతి పౌరుడు తన హక్కులను తెలుసుకోవాలి", "హైదరాబాద్ తెలంగాణ రాష్ట్ర రాజధాని", "రైతులకు ప్రభుత్వం సహాయం అందిస్తుంది",
    };
    internal static readonly string[] Hi =
    {
        "भारत एक विशाल देश है", "हमारी सरकार नागरिकों के अधिकारों की रक्षा करती है", "यह दस्तावेज़ सुरक्षित रखा जाएगा",
        "प्रत्येक नागरिक को शिक्षा का अधिकार है", "राजस्थान की राजधानी जयपुर है", "किसानों को सरकार सहायता देती है",
    };
    internal static readonly string[] Ta =
    {
        "தமிழ் மொழி மிகவும் பழமையானது", "இந்தியா எனது தாய்நாடு", "தமிழ்நாடு அரசு அறிவிப்பு",
        "ஒவ்வொரு குடிமகனுக்கும் உரிமைகள் உண்டு", "சென்னை தமிழ்நாட்டின் தலைநகரம்", "விவசாயிகளுக்கு அரசு உதவி செய்கிறது",
    };
    internal static readonly string[] Kn =
    {
        "ಕನ್ನಡ ಭಾಷೆ ತುಂಬಾ ಸುಂದರವಾಗಿದೆ", "ಭಾರತ ನನ್ನ ತಾಯಿನಾಡು", "ಕರ್ನಾಟಕ ಸರ್ಕಾರದ ಆದೇಶ",
        "ಪ್ರತಿ ನಾಗರಿಕನಿಗೂ ಹಕ್ಕುಗಳಿವೆ", "ಬೆಂಗಳೂರು ಕರ್ನಾಟಕದ ರಾಜಧಾನಿ", "ರೈತರಿಗೆ ಸರ್ಕಾರ ಸಹಾಯ ಮಾಡುತ್ತದೆ",
    };
    internal static readonly string[] En =
    {
        "Registration of sale deed number 4521", "Survey No. 123/4A, Extent 2.35 Acres", "Government of India notification",
        "The applicant shall appear before the court", "Total amount payable Rs.5000 only", "Date of issue 12-05-2023",
    };

    internal static (string Text, string Font)[] Page(string[] pool, string font, int count, int seed)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => (pool[rnd.Next(pool.Length)] + " " + pool[rnd.Next(pool.Length)], font)).ToArray();
    }

    static readonly OcrLanguage[] Auto = { OcrLanguage.Auto };

    static OcrLanguage Lang(string font) => font switch
    {
        TeFont => OcrLanguage.Telugu, HiFont => OcrLanguage.Hindi, TaFont => OcrLanguage.Tamil, KnFont => OcrLanguage.Kannada, _ => OcrLanguage.English,
    };

    /// <summary>Renders the rows, one per line, on an A4-width page.</summary>
    static SKBitmap RenderRows(IReadOnlyList<(string Text, string Font)> rows, float size = 46) =>
        TestEnv.RenderMixed(rows, 2480, (int)(size * 1.9f * rows.Count + size * 3), size, 1.9f, 150);

    static double MeanCer(IReadOnlyList<(string Text, string Font)> rows, OcrPage page)
    {
        double sum = 0;
        for (int i = 0; i < rows.Count; i++) sum += i < page.Lines.Count ? TestEnv.Cer(rows[i].Text, page.Lines[i].Text) : 1;
        return sum / rows.Count;
    }

    [ModelsFact]
    public void Single_script_pages_read_like_explicit_mode_and_report_their_language()
    {
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        foreach (var (pool, font) in new[] { (Te, TeFont), (Hi, HiFont), (Ta, TaFont), (Kn, KnFont) })
        {
            var lang = Lang(font);
            var rows = Page(pool, font, 14, 11);
            using var bmp = RenderRows(rows);
            var explicitPage = eng.Recognize(bmp, new[] { lang, OcrLanguage.English });
            var autoPage = eng.Recognize(bmp, Auto);
            double ce = MeanCer(rows, explicitPage), ca = MeanCer(rows, autoPage);
            _out.WriteLine($"{lang}: explicit CER {ce:F4}, auto CER {ca:F4}, detected {string.Join(",", autoPage.DetectedLanguages)}");
            Assert.Equal(rows.Length, autoPage.Lines.Count);
            Assert.True(ca <= ce + 0.01, $"{lang}: auto CER {ca:F4} vs explicit {ce:F4}");
            Assert.Equal(new[] { lang }, autoPage.DetectedLanguages);
            Assert.All(autoPage.Lines, l => Assert.Equal(lang, l.Language));
        }
    }

    [ModelsFact]
    public void Mixed_telugu_and_english_page()
    {
        var rows = new (string, string)[]
        {
            ("రిజిస్ట్రేషన్ నంబర్ 4521/2023", TeFont), ("Registration of sale deed number 4521", EnFont),
            ("ఆంధ్ర ప్రదేశ్ ప్రభుత్వం Government of Andhra Pradesh", TeFont), ("The applicant shall appear before the court", EnFont),
            ("భారతదేశం నా మాతృభూమి", TeFont), ("Total amount payable Rs.5000 only", EnFont),
            ("ప్రతి పౌరుడు తన హక్కులను తెలుసుకోవాలి", TeFont), ("Date of issue 12-05-2023", EnFont),
        };
        using var bmp = RenderRows(rows);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, Auto);
        foreach (var l in page.Lines) _out.WriteLine($"[{l.Language} {l.Confidence:F2}] {l.Text}");
        Assert.Equal(rows.Length, page.Lines.Count);
        Assert.Equal(new[] { OcrLanguage.Telugu, OcrLanguage.English }.OrderBy(x => x), page.DetectedLanguages.OrderBy(x => x));
        for (int i = 0; i < rows.Length; i++)
            Assert.Equal(Lang(rows[i].Item2), page.Lines[i].Language);
        Assert.True(MeanCer(rows, page) < 0.06, $"CER {MeanCer(rows, page):F3}");
    }

    [ModelsFact]
    public void Mixed_hindi_and_english_page()
    {
        var rows = new (string, string)[]
        {
            ("भारत सरकार Government of India", HiFont), ("Registration of sale deed number 4521", EnFont),
            ("यह दस्तावेज़ सुरक्षित रखा जाएगा", HiFont), ("The applicant shall appear before the court", EnFont),
            ("राजस्थान की राजधानी जयपुर है", HiFont), ("Total amount payable Rs.5000 only", EnFont),
            ("प्रत्येक नागरिक को शिक्षा का अधिकार है", HiFont), ("Date of issue 12-05-2023", EnFont),
        };
        using var bmp = RenderRows(rows);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, Auto);
        foreach (var l in page.Lines) _out.WriteLine($"[{l.Language} {l.Confidence:F2}] {l.Text}");
        Assert.Equal(rows.Length, page.Lines.Count);
        Assert.Equal(new[] { OcrLanguage.Hindi, OcrLanguage.English }.OrderBy(x => x), page.DetectedLanguages.OrderBy(x => x));
        for (int i = 0; i < rows.Length; i++)
            Assert.Equal(Lang(rows[i].Item2), page.Lines[i].Language);
        Assert.True(MeanCer(rows, page) < 0.06, $"CER {MeanCer(rows, page):F3}");
    }

    [ModelsFact]
    public void English_only_page_is_detected_as_english()
    {
        var rows = En.Select(t => (t, EnFont)).Concat(En.Select(t => (t + " and annexure", EnFont))).ToArray();
        using var bmp = RenderRows(rows);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, Auto);
        Assert.Equal(new[] { OcrLanguage.English }, page.DetectedLanguages);
        Assert.All(page.Lines, l => Assert.Equal(OcrLanguage.English, l.Language));
        Assert.True(MeanCer(rows, page) < 0.06, $"CER {MeanCer(rows, page):F3}");
    }

    [ModelsFact]
    public void Telugu_paragraphs_around_a_hindi_paragraph_each_line_gets_its_own_script()
    {
        var rows = Te.Take(4).Select(t => (Text: t, Font: TeFont))
            .Concat(Hi.Take(4).Select(t => (Text: t, Font: HiFont)))
            .Concat(Te.Skip(2).Take(4).Select(t => (Text: t, Font: TeFont))).ToArray();
        using var bmp = RenderRows(rows);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, Auto);
        foreach (var l in page.Lines) _out.WriteLine($"[{l.Language} {l.Confidence:F2}] {l.Text}");
        Assert.Equal(rows.Length, page.Lines.Count);
        for (int i = 0; i < rows.Length; i++) Assert.Equal(Lang(rows[i].Font), page.Lines[i].Language);
        Assert.Equal(OrderedSet(OcrLanguage.Telugu, OcrLanguage.Hindi), OrderedSet(page.DetectedLanguages.ToArray()));
        Assert.Equal(OcrLanguage.Telugu, page.DetectedLanguages[0]); // more Telugu text than Hindi
        Assert.True(MeanCer(rows, page) < 0.05, $"CER {MeanCer(rows, page):F3}");
    }

    static OcrLanguage[] OrderedSet(params OcrLanguage[] l) => l.OrderBy(x => x).ToArray();

    [ModelsFact]
    public void Telugu_with_a_kannada_paragraph_and_tamil_lines_confusion_stays_low()
    {
        // Telugu and Kannada glyphs are close; the Kannada model is the old PP-OCRv3. This test records how a mixed Telugu/Kannada
        // page is labelled and only requires that most lines are right and that no Telugu line is turned into Kannada text.
        var rows = Te.Take(5).Select(t => (Text: t, Font: TeFont)).Concat(Kn.Take(4).Select(t => (Text: t, Font: KnFont))).Concat(Ta.Take(2).Select(t => (Text: t, Font: TaFont))).ToArray();
        using var bmp = RenderRows(rows);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, Auto);
        int right = 0, teAsKn = 0;
        for (int i = 0; i < rows.Length && i < page.Lines.Count; i++)
        {
            if (page.Lines[i].Language == Lang(rows[i].Font)) right++;
            if (rows[i].Font == TeFont && page.Lines[i].Language == OcrLanguage.Kannada) teAsKn++;
            _out.WriteLine($"want {Lang(rows[i].Font)} got {page.Lines[i].Language}");
        }
        _out.WriteLine($"{right}/{rows.Length} lines labelled correctly");
        Assert.Equal(rows.Length, page.Lines.Count);
        // Measured: 1 of 5 (Te[4], where the Kannada model scores 0.05 better than the Telugu model on this render). A page-level prior
        // (toss-ups go to the page's dominant script) was tried and made it worse: it turned Telugu paragraphs on Kannada pages into
        // Kannada. Over 8 page types x 4 seeds (168 Telugu and 184 Kannada lines) Telugu->Kannada happens on 2 lines, Kannada->Telugu on 1.
        Assert.True(teAsKn <= 1, $"{teAsKn} Telugu lines were read as Kannada");
        Assert.True(right >= rows.Length - 3, $"{right}/{rows.Length} lines labelled correctly");
    }

    [ModelsFact]
    public void Auto_restricted_to_listed_indic_languages_and_RecognizeLine()
    {
        var rows = new (string, string)[] { (Te[0], TeFont), (Hi[0], HiFont), (Te[1], TeFont), (Hi[1], HiFont) };
        using var bmp = RenderRows(rows);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var page = eng.Recognize(bmp, new[] { OcrLanguage.Auto, OcrLanguage.Telugu, OcrLanguage.Hindi, OcrLanguage.English });
        for (int i = 0; i < rows.Length; i++) Assert.Equal(Lang(rows[i].Item2), page.Lines[i].Language);
        var only = eng.Recognize(bmp, new[] { OcrLanguage.Auto, OcrLanguage.Telugu }); // Hindi not a candidate: never labelled Hindi
        Assert.DoesNotContain(only.Lines, l => l.Language == OcrLanguage.Hindi);
        var line = eng.RecognizeLine(bmp, page.Lines[1].Box, Auto);
        Assert.Equal(OcrLanguage.Hindi, line.Language);
        Assert.True(TestEnv.Cer(Hi[0], line.Text) < 0.05);
    }

    static string Sig(OcrPage p) => string.Join("\n", p.Lines.Select(l => $"{l.Language}:{l.Text}"));

    /// <summary>Small pages of every script, with English lines mixed into some; the English-only page has numbers that other recognizers misread.</summary>
    static Dictionary<string, SKBitmap> DeterminismPages()
    {
        (string, string)[] WithEn(string[] pool, string font, int seed)
        {
            var rows = Page(pool, font, 8, seed);
            rows[3] = (En[4], EnFont); rows[6] = (En[1], EnFont);
            return rows;
        }
        return new Dictionary<string, SKBitmap>
        {
            ["te"] = RenderRows(WithEn(Te, TeFont, 21)), ["hi"] = RenderRows(WithEn(Hi, HiFont, 22)), ["ta"] = RenderRows(Page(Ta, TaFont, 8, 23)),
            ["kn"] = RenderRows(Page(Kn, KnFont, 8, 24)), ["en"] = RenderRows(En.Concat(En).Select(t => (t, EnFont)).ToArray()),
        };
    }

    [ModelsFact]
    public void A_page_reads_the_same_whatever_was_recognized_before_and_under_concurrency()
    {
        var pages = DeterminismPages();
        try
        {
            string[] keys = pages.Keys.ToArray();
            // reference: each page on a brand-new engine
            var fresh = new Dictionary<string, string>();
            foreach (var k in keys) { using var e = new OcrEngine(TestEnv.ModelsDir!); fresh[k] = Sig(e.Recognize(pages[k], Auto)); }

            // history: every ordered pair on one engine (the old engine-level script hint made English after Tamil read "5o00")
            using (var e = new OcrEngine(TestEnv.ModelsDir!))
                foreach (var prev in keys)
                {
                    e.Recognize(pages[prev], Auto);
                    foreach (var k in keys) Assert.True(fresh[k] == Sig(e.Recognize(pages[k], Auto)), $"{k} after {prev} differs from a fresh engine");
                }

            // concurrency: four threads, mixed pages, one shared engine
            using var shared = new OcrEngine(TestEnv.ModelsDir!);
            int bad = 0;
            Parallel.For(0, keys.Length * 3, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                var k = keys[i % keys.Length];
                if (Sig(shared.Recognize(pages[k], Auto)) != fresh[k]) Interlocked.Increment(ref bad);
            });
            Assert.Equal(0, bad);

            // the English-only page is read by the English recognizer: like explicit English, and "Rs.5000" is intact
            var enPage = shared.Recognize(pages["en"], Auto);
            var enExplicit = shared.Recognize(pages["en"], new[] { OcrLanguage.English });
            Assert.Equal(Sig(enExplicit).Replace("English:", ""), Sig(enPage).Replace("English:", ""));
            Assert.Contains("Rs.5000", enPage.Text);
        }
        finally { foreach (var b in pages.Values) b.Dispose(); }
    }

    [ModelsFact]
    public async Task The_script_hint_lives_for_one_file_only()
    {
        // RecognizeFileAsync keeps the previous page's script as a hint for the pages of THAT file; a second file starts from nothing,
        // so an English file read right after a Hindi one reads like a fresh English page.
        var pages = DeterminismPages();
        var dir = Path.Combine(Path.GetTempPath(), "indicocr-hint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string Save(string k)
            {
                var path = Path.Combine(dir, k + ".png");
                using var fs = File.Create(path);
                pages[k].Encode(fs, SKEncodedImageFormat.Png, 100);
                return path;
            }
            using var eng = new OcrEngine(TestEnv.ModelsDir!);
            var viaFile = new Dictionary<string, string>();
            foreach (var k in new[] { "hi", "en", "kn", "en" })
                await foreach (var (img, page) in eng.RecognizeFileAsync(Save(k), Auto)) { viaFile[k] = Sig(page); img.Dispose(); }
            foreach (var k in new[] { "hi", "en", "kn" })
            {
                using var reloaded = SKBitmap.Decode(Path.Combine(dir, k + ".png"));
                Assert.Equal(Sig(eng.Recognize(reloaded, Auto)), viaFile[k]);
            }
        }
        finally { Directory.Delete(dir, true); foreach (var b in pages.Values) b.Dispose(); }
    }

    [ModelsFact]
    public void Auto_page_timing_against_explicit_mode()
    {
        // Dense A4 Telugu page (34 lines, as A4_page_timing_and_accuracy). Min of several alternating runs, so other load on the machine
        // does not decide the result. The design target is under 1.35x; the assertion allows noise.
        var rows = Page(Te, TeFont, 34, 7);
        using var bmp = TestEnv.Render(rows, 2480, 3508, 46, 1.9f, 150);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var explicitLangs = new[] { OcrLanguage.Telugu, OcrLanguage.English };
        eng.Recognize(bmp, explicitLangs); eng.Recognize(bmp, Auto); // warm-up (loads the sessions)
        var te = new List<double>(); var ta = new List<double>();
        for (int i = 0; i < 4; i++)
        {
            te.Add(Wall(() => eng.Recognize(bmp, explicitLangs)));
            ta.Add(Wall(() => eng.Recognize(bmp, Auto)));
        }
        double ratio = ta.Min() / te.Min();
        _out.WriteLine($"explicit min {te.Min():F2}s [{string.Join(",", te.Select(x => x.ToString("F2")))}]  auto min {ta.Min():F2}s [{string.Join(",", ta.Select(x => x.ToString("F2")))}]  ratio {ratio:F2}");
        Assert.True(ratio < 1.35, $"auto/explicit = {ratio:F2} (explicit {te.Min():F2}s, auto {ta.Min():F2}s)");
    }

    [ModelsFact]
    public void Auto_timing_per_script_report()
    {
        // Per script, a dense page: explicit mode vs Auto. A single-page call has no script hint, so this is the full cost (sample vote
        // included). Min of three alternating runs. Measured: Telugu, Hindi, Tamil 1.1x to 1.5x; Kannada 1.5x to 1.7x (it was 3.6x when the Telugu
        // model re-read every Kannada line, 2.1x expected). The Kannada bound is 2.0 and says so in the CLI help.
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        foreach (var (pool, font) in new[] { (Hi, HiFont), (Ta, TaFont), (Kn, KnFont), (Te, TeFont) })
        {
            var lang = Lang(font);
            using var bmp = TestEnv.Render(Page(pool, font, 30, 5), 2480, 3200, 46, 1.9f, 150);
            var ex = new[] { lang, OcrLanguage.English };
            eng.Recognize(bmp, ex); eng.Recognize(bmp, Auto); // warm-up
            double tEx = double.MaxValue, tAuto = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                tEx = Math.Min(tEx, Wall(() => eng.Recognize(bmp, ex)));
                tAuto = Math.Min(tAuto, Wall(() => eng.Recognize(bmp, Auto)));
            }
            _out.WriteLine($"{lang}: explicit {tEx:F2}s  auto {tAuto:F2}s = {tAuto / tEx:F2}x");
            Assert.True(tAuto / tEx < (lang == OcrLanguage.Kannada ? 2.0 : 1.6), $"{lang}: auto {tAuto:F2}s vs explicit {tEx:F2}s");
        }
    }

    static double Wall(Action a) { var sw = System.Diagnostics.Stopwatch.StartNew(); a(); return sw.Elapsed.TotalSeconds; }

    [ModelsOnlyFact]
    public void Auto_is_not_an_available_language_and_needs_an_indic_model()
    {
        Assert.DoesNotContain(OcrLanguage.Auto, OcrEngine.AvailableLanguages(TestEnv.ModelsDir!));
        var dir = Path.Combine(Path.GetTempPath(), "indicocr-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Path.Combine(TestEnv.ModelsDir!, "det.onnx"), Path.Combine(dir, "det.onnx"));
            using var eng = new OcrEngine(dir);
            using var bmp = new SKBitmap(200, 100);
            Assert.Throws<FileNotFoundException>(() => eng.Recognize(bmp, Auto));
        }
        finally { Directory.Delete(dir, true); }
    }
}
