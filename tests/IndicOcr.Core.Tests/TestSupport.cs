using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Xunit;

namespace IndicOcr.Core.Tests;

/// <summary>Runs only when INDICOCR_MODELS points at a models folder with the detector and Noto fonts exist.</summary>
public sealed class ModelsFactAttribute : FactAttribute
{
    public ModelsFactAttribute()
    {
        if (TestEnv.ModelsDir == null) Skip = "INDICOCR_MODELS not set (or folder missing); integration test skipped.";
        else if (TestEnv.FontsDir == null) Skip = "Noto fonts not found (set INDICOCR_TEST_FONTS).";
    }
}

/// <summary>Skipped (reported as skipped, not passed) when the Noto test fonts cannot be found; set INDICOCR_TEST_FONTS.</summary>
public sealed class FontsFactAttribute : FactAttribute
{
    public FontsFactAttribute()
    {
        if (TestEnv.FontsDir == null) Skip = "Noto fonts not found (set INDICOCR_TEST_FONTS).";
    }
}

public static class TestEnv
{
    static readonly Lazy<string?> Effective = new(ResolveModels);

    /// <summary>The INDICOCR_MODELS folder, or null when unset or missing.</summary>
    public static string? ModelsDir => Effective.Value;

    static string? ResolveModels()
    {
        var d = Environment.GetEnvironmentVariable("INDICOCR_MODELS");
        return string.IsNullOrEmpty(d) || !Directory.Exists(d) ? null : d;
    }

    public static string? FontsDir
    {
        get
        {
            var cands = new[] { Environment.GetEnvironmentVariable("INDICOCR_TEST_FONTS"), "/usr/share/fonts/truetype/noto" };
            return cands.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "NotoSansTelugu-Regular.ttf")));
        }
    }

    public static string Font(string file) => Path.Combine(FontsDir!, file);

    /// <summary>Renders each line with HarfBuzz shaping (needed for Indic conjuncts) on white.</summary>
    public static SKBitmap Render(IReadOnlyList<(string Text, string FontFile)> lines, int width, int height, float size, float lineGap = 1.6f, float left = 60)
    {
        var bmp = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        float y = size * 1.6f;
        foreach (var (text, file) in lines)
        {
            using var tf = SKTypeface.FromFile(Font(file));
            using var font = new SKFont(tf, size);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawShapedText(text, left, y, SKTextAlign.Left, font, paint);
            y += size * lineGap;
        }
        return bmp;
    }

    /// <summary>
    /// Like Render, but each line is split into Latin runs (letters, digits, punctuation, spaces; drawn with NotoSans) and
    /// Indic runs (drawn with the given Indic font), so mixed-script lines have real glyphs for both.
    /// </summary>
    public static SKBitmap RenderMixed(IReadOnlyList<(string Text, string IndicFontFile)> lines, int width, int height, float size, float lineGap = 1.6f, float left = 60)
    {
        var bmp = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        float y = size * 1.6f;
        using var latinTf = SKTypeface.FromFile(Font("NotoSans-Regular.ttf"));
        using var latinFont = new SKFont(latinTf, size);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        foreach (var (text, file) in lines)
        {
            using var indicTf = SKTypeface.FromFile(Font(file));
            using var indicFont = new SKFont(indicTf, size);
            float x = left;
            int i = 0;
            while (i < text.Length)
            {
                bool latin = text[i] < 0x0900;
                int j = i;
                while (j < text.Length && (text[j] < 0x0900) == latin) j++;
                var run = text[i..j];
                var (tf, font) = latin ? (latinTf, latinFont) : (indicTf, indicFont);
                canvas.DrawShapedText(run, x, y, SKTextAlign.Left, font, paint);
                using var shaper = new SkiaSharp.HarfBuzz.SKShaper(tf);
                x += shaper.Shape(run, font).Width;
                i = j;
            }
            y += size * lineGap;
        }
        return bmp;
    }

    /// <summary>Character error rate over code points, ignoring whitespace (the Telugu model rarely emits word spaces).</summary>
    public static double Cer(string reference, string hypothesis)
    {
        var a = ToCodepoints(Norm(reference));
        var b = ToCodepoints(Norm(hypothesis));
        if (a.Length == 0) return b.Length == 0 ? 0 : 1;
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = cur;
        }
        return (double)prev[b.Length] / a.Length;
    }

    static string Norm(string s) => string.Concat(s.Normalize(NormalizationForm.FormC).Where(c => !char.IsWhiteSpace(c)));
    static int[] ToCodepoints(string s)
    {
        var l = new List<int>();
        for (int i = 0; i < s.Length; i++)
        {
            int cp = char.ConvertToUtf32(s, i);
            if (char.IsHighSurrogate(s[i])) i++;
            l.Add(cp);
        }
        return l.ToArray();
    }
}
