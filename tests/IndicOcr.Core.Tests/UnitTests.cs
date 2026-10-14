using System.Drawing;
using IndicOcr.Core.Internal;
using Xunit;

namespace IndicOcr.Core.Tests;

public class CtcTests
{
    [Fact]
    public void CharTable_has_blank_first_and_space_last()
    {
        var t = CharTable.FromLines(new[] { "a", "b" });
        Assert.Equal(4, t.ClassCount);
        Assert.Equal("", t[0]);
        Assert.Equal("a", t[1]);
        Assert.Equal(" ", t[3]);
    }

    [Fact]
    public void Validate_rejects_mismatched_class_count()
    {
        var t = CharTable.FromLines(new[] { "a", "b" });
        t.Validate(4);
        Assert.Throws<InvalidDataException>(() => t.Validate(5));
        Assert.Throws<InvalidDataException>(() => t.Validate(3));
    }

    static float[] OneHot(int[] classes, float[] p, int c)
    {
        var d = new float[classes.Length * c];
        for (int i = 0; i < classes.Length; i++)
        {
            float rest = (1 - p[i]) / (c - 1);
            for (int k = 0; k < c; k++) d[i * c + k] = k == classes[i] ? p[i] : rest;
        }
        return d;
    }

    [Fact]
    public void Decode_collapses_repeats_and_drops_blanks()
    {
        var t = CharTable.FromLines(new[] { "a", "b" });
        // a a blank a b b blank space
        var seq = new[] { 1, 1, 0, 1, 2, 2, 0, 3 };
        var p = new[] { .9f, .8f, .99f, .7f, .6f, .9f, .99f, .5f };
        var (text, conf) = CtcDecoder.Decode(OneHot(seq, p, 4), seq.Length, 4, t);
        Assert.Equal("aab ", text);
        // kept: first a (.9), second a after blank (.7), b first of run (.6), space (.5)
        Assert.Equal((0.9 + 0.7 + 0.6 + 0.5) / 4, conf, 3);
    }

    [Fact]
    public void Decode_all_blank_gives_empty_and_zero_confidence()
    {
        var t = CharTable.FromLines(new[] { "a" });
        var (text, conf) = CtcDecoder.Decode(OneHot(new[] { 0, 0, 0 }, new[] { .9f, .9f, .9f }, 3), 3, 3, t);
        Assert.Equal("", text);
        Assert.Equal(0f, conf);
    }

    [Fact]
    public void Load_reads_utf8_dictionary_lines()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "అ\nఆ\r\nక\n");
            var t = CharTable.Load(f);
            Assert.Equal(5, t.ClassCount);
            Assert.Equal("ఆ", t[2]);
        }
        finally { File.Delete(f); }
    }

    [ModelsOnlyFact]
    public void Real_dictionaries_load_when_models_present()
    {
        var dir = TestEnv.ModelsDir!;
        foreach (var p in new[] { "te", "hi", "ta", "kn", "en" })
        {
            var t = CharTable.Load(Path.Combine(dir, "dict_" + p + ".txt"));
            Assert.True(t.ClassCount > 50, p);
            Assert.Equal(" ", t[t.ClassCount - 1]);
        }
    }
}

public class PreprocessTests
{
    [Theory]
    [InlineData(3000, 2000, 1920, 1920, 1280)]
    [InlineData(2480, 3508, 1920, 1344, 1920)]
    [InlineData(1000, 500, 1920, 992, 512)]
    [InlineData(10, 10, 1920, 32, 32)]
    public void DetSize_limits_long_side_and_rounds_to_32(int w, int h, int max, int ew, int eh)
    {
        var (rw, rh) = Preprocess.DetSize(w, h, max);
        Assert.Equal((ew, eh), (rw, rh));
        Assert.Equal(0, rw % 32);
        Assert.Equal(0, rh % 32);
    }

    [Fact]
    public void RecWidth_keeps_aspect_at_height_48()
    {
        Assert.Equal(48, Preprocess.RecWidth(1.0));
        Assert.Equal(480, Preprocess.RecWidth(10.0));
        Assert.Equal(49, Preprocess.RecWidth(1.01));
    }

    [Fact]
    public void RecBatchTensor_normalises_and_pads_with_zero()
    {
        var bgr = new byte[48 * 2 * 3];
        for (int i = 0; i < bgr.Length; i += 3) { bgr[i] = 255; bgr[i + 1] = 0; bgr[i + 2] = 255; }
        var crop = new Preprocess.LineCrop(bgr, 2, 2.0 / 48);
        var t = Preprocess.RecBatchTensor(new[] { crop }, 5);
        int plane = 48 * 5;
        Assert.Equal(3 * plane, t.Length);
        Assert.Equal(1.0, (double)t[0], 4);              // B=255 -> 1
        Assert.Equal(-1.0, (double)t[plane], 4);         // G=0 -> -1
        Assert.Equal(1.0, (double)t[2 * plane], 4);      // R=255 -> 1
        Assert.Equal(0f, t[2]);                 // padding
    }

    [Fact]
    public void Crop_returns_height_48_and_scaled_width()
    {
        int w = 200, h = 100;
        var px = new byte[w * h * 4];
        Array.Fill(px, (byte)255);
        var quad = new[] { new Pt(10, 10), new Pt(110, 10), new Pt(110, 30), new Pt(10, 30) }; // 100 x 20
        var c = Preprocess.Crop(px, w, h, quad);
        Assert.Equal(240, c.Width);
        Assert.Equal(240 * 48 * 3, c.Bgr.Length);
        Assert.All(c.Bgr, b => Assert.Equal(255, b));
    }
}

public class DbPostProcessTests
{
    static float[] Map(int w, int h, Action<float[], int> fill)
    {
        var m = new float[w * h];
        fill(m, w);
        return m;
    }

    [Fact]
    public void Unclip_expands_by_area_times_ratio_over_perimeter()
    {
        var box = new[] { new Pt(0, 0), new Pt(100, 0), new Pt(100, 20), new Pt(0, 20) };
        var g = Geometry.Unclip(box, 1.5);
        double d = 2000 * 1.5 / 240; // 12.5
        Assert.Equal(-d, g[0].X, 6);
        Assert.Equal(-d, g[0].Y, 6);
        Assert.Equal(100 + d, g[2].X, 6);
        Assert.Equal(20 + d, g[2].Y, 6);
    }

    [Fact]
    public void Extracts_axis_aligned_box_with_unclip_and_scaling()
    {
        int w = 200, h = 100;
        var prob = Map(w, h, (m, ww) => { for (int y = 40; y <= 60; y++) for (int x = 20; x <= 120; x++) m[y * ww + x] = 0.9f; });
        var boxes = DbPostProcessor.Extract(prob, w, h, 400, 200, new DbParams());
        Assert.Single(boxes);
        var b = boxes[0].Box;
        Assert.True(boxes[0].Score > 0.8f);
        // component 100x20 (pixel centres) -> d = 2000*1.5/240 = 12.5 -> x 7.5..132.5, y 27.5..72.5, then x2
        Assert.InRange(b[0].X, 12, 18);
        Assert.InRange(b[0].Y, 52, 58);
        Assert.InRange(b[2].X, 262, 268);
        Assert.InRange(b[2].Y, 142, 148);
        Assert.True(b[0].X < b[1].X && b[0].Y < b[3].Y); // tl,tr,br,bl order
    }

    [Fact]
    public void Rejects_low_score_and_tiny_components()
    {
        int w = 200, h = 100;
        var low = Map(w, h, (m, ww) => { for (int y = 40; y <= 60; y++) for (int x = 20; x <= 120; x++) m[y * ww + x] = 0.4f; });
        Assert.Empty(DbPostProcessor.Extract(low, w, h, w, h, new DbParams()));
        var tiny = Map(w, h, (m, ww) => { for (int y = 40; y <= 41; y++) for (int x = 20; x <= 60; x++) m[y * ww + x] = 0.95f; });
        Assert.Empty(DbPostProcessor.Extract(tiny, w, h, w, h, new DbParams()));
    }

    [Fact]
    public void Separates_two_lines_and_finds_rotated_rectangle()
    {
        int w = 300, h = 200;
        double ang = 10 * Math.PI / 180, cx = 150, cy = 100, hw = 80, hh = 10;
        var prob = Map(w, h, (m, ww) =>
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double dx = x - cx, dy = y - cy;
                    double u = dx * Math.Cos(ang) + dy * Math.Sin(ang), v = -dx * Math.Sin(ang) + dy * Math.Cos(ang);
                    if (Math.Abs(u) <= hw && Math.Abs(v) <= hh) m[y * ww + x] = 0.9f;
                }
        });
        var boxes = DbPostProcessor.Extract(prob, w, h, w, h, new DbParams { UnclipRatio = 0.0001 });
        Assert.Single(boxes);
        var b = boxes[0].Box;
        double top = (b[1] - b[0]).Length, side = (b[3] - b[0]).Length;
        Assert.InRange(top, 2 * hw - 3, 2 * hw + 3);
        Assert.InRange(side, 2 * hh - 3, 2 * hh + 3);
        double slope = Math.Atan2(b[1].Y - b[0].Y, b[1].X - b[0].X) * 180 / Math.PI;
        Assert.InRange(slope, 8, 12);
    }

    [Fact]
    public void MinAreaRect_of_square_hull_is_the_square()
    {
        var hull = Geometry.ConvexHull(new[] { new Pt(0, 0), new Pt(10, 0), new Pt(10, 10), new Pt(0, 10), new Pt(5, 5) });
        Assert.Equal(4, hull.Count);
        var r = Geometry.MinAreaRect(hull);
        Assert.Equal(100, Geometry.PolygonArea(r), 6);
    }
}

public class LineOrderingTests
{
    [Fact]
    public void Orders_rows_top_to_bottom_and_left_to_right()
    {
        var boxes = new[]
        {
            ("r2c2", new RectangleF(200, 100, 80, 30)),
            ("r1c2", new RectangleF(200, 10, 80, 30)),
            ("r2c1", new RectangleF(10, 105, 80, 30)),   // slightly lower than its neighbour
            ("r1c1", new RectangleF(10, 14, 80, 28)),
            ("r3", new RectangleF(10, 200, 300, 30)),
        };
        var ordered = LineOrdering.Order(boxes, b => b.Item2).Select(b => b.Item1);
        Assert.Equal(new[] { "r1c1", "r1c2", "r2c1", "r2c2", "r3" }, ordered);
    }
}

public class InputFilesTests
{
    [Fact]
    public void IsSupported_checks_extension_case_insensitively()
    {
        Assert.True(InputFiles.IsSupported("a.PNG"));
        Assert.True(InputFiles.IsSupported("x/y.tiff"));
        Assert.True(InputFiles.IsSupported("scan.pdf"));
        Assert.False(InputFiles.IsSupported("notes.txt"));
        Assert.False(InputFiles.IsSupported(""));
    }

    [Fact]
    public void Enumerate_lists_supported_files_in_natural_order_and_honours_recursive()
    {
        var root = Path.Combine(Path.GetTempPath(), "indicocr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        try
        {
            foreach (var n in new[] { "p10.png", "p2.png", "p1.JPG", "readme.txt" }) File.WriteAllText(Path.Combine(root, n), "x");
            File.WriteAllText(Path.Combine(root, "sub", "deep.pdf"), "x");
            var flat = InputFiles.Enumerate(root, false).Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "p1.JPG", "p2.png", "p10.png" }, flat);
            Assert.Equal(4, InputFiles.Enumerate(root, true).Count());
            Assert.Single(InputFiles.Enumerate(Path.Combine(root, "p2.png"), false));
            Assert.Empty(InputFiles.Enumerate(Path.Combine(root, "readme.txt"), false));
        }
        finally { Directory.Delete(root, true); }
    }
}
