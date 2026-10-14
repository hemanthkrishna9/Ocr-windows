using System.Drawing;
using IndicOcr.Core.Internal;
using Xunit;

namespace IndicOcr.Core.Tests;

public class AutoRoutingUnitTests
{
    static Pt[] Box(double x, double y, double w, double h = 40) => new[] { new Pt(x, y), new Pt(x + w, y), new Pt(x + w, y + h), new Pt(x, y + h) };

    [Fact]
    public void Sample_is_spread_over_the_page_and_skips_short_lines()
    {
        var quads = new List<Pt[]>();
        for (int i = 0; i < 40; i++) quads.Add(Box(100, 50 + i * 60, i % 5 == 0 ? 120 : 1000));
        var s = ScriptRouting.PickSample(quads, 6);
        Assert.Equal(6, s.Count);
        Assert.All(s, i => Assert.True(i % 5 != 0)); // the short ones are not sampled
        var ys = s.Select(i => quads[i][0].Y).ToList();
        Assert.Equal(ys.OrderBy(y => y), ys);          // top to bottom
        Assert.True(ys.Max() - ys.Min() > 1500);        // spans the page
    }

    [Fact]
    public void Sample_of_a_short_page_is_all_lines()
    {
        var quads = new List<Pt[]> { Box(0, 0, 500), Box(0, 100, 500) };
        Assert.Equal(2, ScriptRouting.PickSample(quads, 6).Count);
        Assert.Empty(ScriptRouting.PickSample(new List<Pt[]>(), 6));
    }

    [Fact]
    public void Detected_orders_by_text_amount_and_ignores_stray_marks()
    {
        OcrLine L(string t, OcrLanguage l) => new(t, 0.9f, l, new RectangleF(0, 0, 1, 1), Array.Empty<PointF>());
        var d = OcrEngine.Detected(new[]
        {
            L("తెలుగు భాష చాలా మధురమైనది", OcrLanguage.Telugu), L("Hello", OcrLanguage.English), L("I", OcrLanguage.English),
            L("ా", OcrLanguage.Hindi),
        });
        Assert.Equal(new[] { OcrLanguage.Telugu, OcrLanguage.English }, d);
        Assert.Empty(OcrEngine.Detected(Array.Empty<OcrLine>()));
    }

    // ---- Kannada cross-check and Telugu/Kannada page prior ---------------------------------------------------------------------

    static OcrEngine.Rec R(OcrLanguage l, float conf, float score, float mass = 30, string text = "ಕನ್ನಡ") => new(text, conf, score, mass, l);

    [Fact]
    public void Only_a_very_clean_Kannada_reading_skips_the_Telugu_cross_check()
    {
        var reference = new ScriptRouting.Reference(0.983f, -0.021f);
        Assert.Equal(ScriptRouting.Doubt.None, ScriptRouting.Assess(R(OcrLanguage.Kannada, 0.98f, -0.02f), reference));
        Assert.Equal(ScriptRouting.Doubt.None, ScriptRouting.Assess(R(OcrLanguage.Kannada, 0.95f, -0.038f), reference));
        Assert.Equal(ScriptRouting.Doubt.Twin, ScriptRouting.Assess(R(OcrLanguage.Kannada, 0.94f, -0.03f), reference));   // Telugu read as Kannada reached 0.94
        Assert.Equal(ScriptRouting.Doubt.Twin, ScriptRouting.Assess(R(OcrLanguage.Kannada, 0.97f, -0.05f), reference));   // clean confidence, weak Score
        Assert.Equal(ScriptRouting.Doubt.Twin, ScriptRouting.Assess(R(OcrLanguage.Kannada, 0.97f, -0.035f), new(0.99f, -0.005f))); // trails the page's Kannada reference
        Assert.Equal(ScriptRouting.Doubt.Strong, ScriptRouting.Assess(R(OcrLanguage.Kannada, 0.70f, -0.2f), reference));
        Assert.Equal(ScriptRouting.Doubt.None, ScriptRouting.Assess(R(OcrLanguage.Telugu, 0.97f, -0.04f), new(0.978f, -0.044f)));
        Assert.Equal(ScriptRouting.Doubt.Twin, ScriptRouting.Assess(R(OcrLanguage.Telugu, 0.93f, -0.05f, text: "తెలుగు"), new(0.978f, -0.044f)));
    }
}
