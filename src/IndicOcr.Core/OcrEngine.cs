using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using IndicOcr.Core.Internal;
using SkiaSharp;

namespace IndicOcr.Core;

/// <summary>
/// PaddleOCR (PP-OCRv5 mobile; Kannada PP-OCRv3) pipeline on ONNX Runtime: DB detection, line crop, CTC recognition per language.
/// <para>
/// Threading: one engine instance may be shared; <see cref="Recognize"/>, <see cref="RecognizeLine"/> and
/// <see cref="RecognizeFileAsync"/> are safe to call concurrently from several threads (they share the loaded sessions, which is
/// what ONNX Runtime supports). Recognizer sessions are loaded lazily on first use and cached by language; a failed load
/// (missing or damaged file) is not cached, so the next call retries. <see cref="Dispose"/> must only be called when no
/// recognition is in flight: it releases the native sessions, and a call still running on another thread would fail or crash.
/// </para>
/// </summary>
public sealed class OcrEngine : IDisposable
{
    const string DetFile = "det.onnx";

    readonly string _dir;
    readonly string _detPath;
    readonly OcrEngineOptions _opt;
    readonly ConcurrentDictionary<OcrLanguage, Recognizer> _recs = new();
    readonly object _loadLock = new();
    Detector? _detector;
    volatile bool _disposed;

    public OcrEngine(string modelsDirectory, OcrEngineOptions? options = null)
    {
        if (!Directory.Exists(modelsDirectory))
            throw new DirectoryNotFoundException($"Models directory not found: {modelsDirectory}");
        _dir = modelsDirectory;
        _opt = options ?? new OcrEngineOptions();
        _detPath = Path.Combine(_dir, DetFile);
        if (!File.Exists(_detPath)) throw new FileNotFoundException("Detector model missing", _detPath);
    }

    internal static string ModelPrefix(OcrLanguage l) => l switch
    {
        OcrLanguage.English => "en",
        OcrLanguage.Telugu => "te",
        OcrLanguage.Hindi => "hi",
        OcrLanguage.Tamil => "ta",
        OcrLanguage.Kannada => "kn",
        _ => throw new ArgumentOutOfRangeException(nameof(l)),
    };

    /// <summary>Languages whose recognizer and dictionary exist in the folder (detector required too).</summary>
    public static IReadOnlyList<OcrLanguage> AvailableLanguages(string modelsDirectory)
    {
        if (!Directory.Exists(modelsDirectory) || !File.Exists(Path.Combine(modelsDirectory, DetFile)))
            return Array.Empty<OcrLanguage>();
        return Enum.GetValues<OcrLanguage>().Where(l =>
        {
            var p = ModelPrefix(l);
            return File.Exists(Path.Combine(modelsDirectory, $"rec_{p}.onnx"))
                && File.Exists(Path.Combine(modelsDirectory, $"dict_{p}.txt"));
        }).ToList();
    }

    /// <summary>True for languages whose recognizer is still an interim model (Kannada: PP-OCRv3, noticeably less accurate than the PP-OCRv5 ones).</summary>
    public static bool IsBeta(OcrLanguage language) => language == OcrLanguage.Kannada;

    // Lazy loaders that do NOT cache failures: a missing or half-copied model file can be fixed and the call retried.
    Detector GetDetector()
    {
        var d = Volatile.Read(ref _detector);
        if (d != null) return d;
        lock (_loadLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _detector ??= new Detector(_detPath, _opt);
        }
    }

    Recognizer GetRecognizer(OcrLanguage l)
    {
        if (_recs.TryGetValue(l, out var r)) return r;
        lock (_loadLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recs.TryGetValue(l, out r)) return r;
            var p = ModelPrefix(l);
            var onnx = Path.Combine(_dir, $"rec_{p}.onnx");
            var dict = Path.Combine(_dir, $"dict_{p}.txt");
            if (!File.Exists(onnx) || !File.Exists(dict))
                throw new FileNotFoundException($"Model files for {l} not found in {_dir}", onnx);
            r = new Recognizer(onnx, dict, _opt); // a throw here leaves nothing cached
            _recs[l] = r;
            return r;
        }
    }

    static IReadOnlyList<OcrLanguage> CheckLangs(IReadOnlyList<OcrLanguage> languages)
    {
        if (languages == null || languages.Count == 0) throw new ArgumentException("At least one language is required.", nameof(languages));
        return languages.Distinct().ToList();
    }

    /// <summary>
    /// Recognises one page. When <see cref="OcrEngineOptions.AutoRotate"/> is on and the page turns out to lie on its side or upside down,
    /// the result's boxes, Width and Height are in the frame of the TURNED (upright) page and <see cref="OcrPage.RotationApplied"/>
    /// gives the clockwise turn (90/180/270) that was applied. Use <see cref="RecognizeOriented"/> to also get the turned bitmap.
    /// </summary>
    public OcrPage Recognize(SKBitmap image, IReadOnlyList<OcrLanguage> languages, int pageIndex = 0)
    {
        var (page, rotated) = RecognizeOriented(image, languages, pageIndex);
        rotated?.Dispose();
        return page;
    }

    /// <summary>
    /// Like <see cref="Recognize"/>, but also returns the bitmap the boxes refer to: the upright copy when the page was turned
    /// (the caller disposes it), or null when no rotation was applied (the boxes then refer to <paramref name="image"/>).
    /// </summary>
    public (OcrPage Page, SKBitmap? Rotated) RecognizeOriented(SKBitmap image, IReadOnlyList<OcrLanguage> languages, int pageIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);
        var langs = CheckLangs(languages);
        var sw = Stopwatch.StartNew();

        SKBitmap current = image;      // the bitmap that px and quads refer to
        SKBitmap? owned = null;        // the same bitmap when we created it by turning the page
        PixelImage px = PixelImage.From(image);
        int turns = 0;
        try
        {
            var quads = GetDetector().Detect(px).Select(b => b.Box).ToList();

            void Turn(int k)
            {
                var next = PageOrientation.Rotate(current, k);
                var nextPx = PixelImage.From(next);
                var mapped = quads.Select(q => PageOrientation.MapQuad(q, k, current.Width, current.Height)).ToList();
                px.Dispose();
                owned?.Dispose();
                px = nextPx; owned = next; current = next; quads = mapped;
                turns = (turns + k) % 4;
            }

            // 1) Quarter turn: when most boxes are tall the page lies on its side.
            if (_opt.AutoRotate && quads.Count >= 4 && PageOrientation.TallFraction(quads) > 0.6)
            {
                int k = ChooseQuarterTurn(px, quads, langs);
                if (k != 0) Turn(k);
            }

            // 2) Recognise. Only when the lines read badly, test the upside-down hypothesis on a small sample.
            var results = RecognizeQuads(px, quads, langs);
            if (_opt.AutoRotate && ShouldTryUpsideDown(results) && UpsideDownReadsBetter(px, quads, results, langs))
            {
                Turn(2);
                results = RecognizeQuads(px, quads, langs);
            }

            var lines = BuildLines(results, quads, langs, keepEmpty: false);
            var ordered = LineOrdering.Order(lines, l => l.Box);
            sw.Stop();
            var page = new OcrPage(pageIndex, current.Width, current.Height, ordered, sw.Elapsed) { RotationApplied = turns * 90 };
            var rotatedBitmap = owned;
            owned = null; // ownership moves to the caller
            return (page, rotatedBitmap);
        }
        finally
        {
            px.Dispose();
            owned?.Dispose();
        }
    }

    public OcrLine RecognizeLine(SKBitmap image, RectangleF box, IReadOnlyList<OcrLanguage> languages)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);
        var langs = CheckLangs(languages);
        using var px = PixelImage.From(image);
        float x0 = Math.Clamp(box.Left, 0, image.Width), x1 = Math.Clamp(box.Right, 0, image.Width);
        float y0 = Math.Clamp(box.Top, 0, image.Height), y1 = Math.Clamp(box.Bottom, 0, image.Height);
        var quad = new[] { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) };
        var quads = new List<Pt[]> { quad };
        var lines = BuildLines(RecognizeQuads(px, quads, langs), quads, langs, keepEmpty: true);
        return lines.Count > 0
            ? lines[0] with { Box = new RectangleF(x0, y0, x1 - x0, y1 - y0) }
            : new OcrLine("", 0f, langs[0], new RectangleF(x0, y0, x1 - x0, y1 - y0), ToPoly(quad));
    }

    // ---- orientation -------------------------------------------------------------------------------------------

    const int OrientationSample = 5;
    /// <summary>Mean per-frame log-probability under which a page is suspected to be upside down (upright pages measured well above it).</summary>
    const double UpsideDownTrigger = -0.12;
    /// <summary>The upside-down reading must beat the upright one by this much (mean per-frame log-probability) to be accepted.</summary>
    const double UpsideDownMargin = 0.10;

    static List<int> LongestQuads(IReadOnlyList<Pt[]> quads, IReadOnlyList<Rec> results, int n)
    {
        var idx = Enumerable.Range(0, quads.Count)
            .Where(i => results.Count == 0 || !string.IsNullOrWhiteSpace(results[i].Text))
            .OrderByDescending(i => { var (w, h) = PageOrientation.Sides(quads[i]); return Math.Max(w, h); })
            .Take(n).ToList();
        return idx;
    }

    /// <summary>Tries a 90 and a 270 degree clockwise turn on the longest boxes and returns the one that reads better.</summary>
    int ChooseQuarterTurn(PixelImage px, List<Pt[]> quads, IReadOnlyList<OcrLanguage> langs)
    {
        var sample = LongestQuads(quads, Array.Empty<Rec>(), OrientationSample);
        double best = double.NegativeInfinity;
        int bestTurn = 3;
        foreach (int k in new[] { 3, 1 }) // 3 first: it is what the crop does anyway for tall boxes
        {
            var q = sample.Select(i => PageOrientation.Reorder(quads[i], k)).ToList();
            double s = MeanScore(RecognizeQuads(px, q, langs));
            if (s > best) { best = s; bestTurn = k; }
        }
        return bestTurn;
    }

    static bool ShouldTryUpsideDown(IReadOnlyList<Rec> results)
    {
        var scored = results.Where(r => !string.IsNullOrWhiteSpace(r.Text)).ToList();
        return scored.Count >= 2 && MeanScore(scored) < UpsideDownTrigger;
    }

    bool UpsideDownReadsBetter(PixelImage px, List<Pt[]> quads, IReadOnlyList<Rec> results, IReadOnlyList<OcrLanguage> langs)
    {
        var sample = LongestQuads(quads, results, OrientationSample);
        if (sample.Count == 0) return false;
        double upright = MeanScore(sample.Select(i => results[i]).ToList());
        var flipped = RecognizeQuads(px, sample.Select(i => PageOrientation.Reorder(quads[i], 2)).ToList(), langs);
        return MeanScore(flipped) > upright + UpsideDownMargin;
    }

    static double MeanScore(IReadOnlyList<Rec> r) => r.Count == 0 ? double.NegativeInfinity : r.Average(x => (double)x.Score);

    // ---- recognition -------------------------------------------------------------------------------------------

    /// <summary>One crop's winning reading. Score = mean per-frame log max-probability (see <see cref="DecodeResult"/>).</summary>
    internal readonly record struct Rec(string Text, float Confidence, float Score, float Mass, OcrLanguage Lang);

    /// <summary>Which recognizers to run for the selected languages.</summary>
    IReadOnlyList<OcrLanguage> RecognizersFor(IReadOnlyList<OcrLanguage> langs)
    {
        // Speed rule: exactly one Indic language + English -> only the Indic recognizer; its dictionary already holds A-Z a-z 0-9.
        if (_opt.EnglishViaIndicRecognizer && langs.Count == 2 && langs.Contains(OcrLanguage.English))
            return new[] { langs.First(l => l != OcrLanguage.English) };
        return langs;
    }

    List<Rec> RecognizeQuads(PixelImage px, List<Pt[]> quads, IReadOnlyList<OcrLanguage> langs)
    {
        var best = new Rec[quads.Count];
        if (quads.Count == 0) return best.ToList();
        var crops = new List<Preprocess.LineCrop>(quads.Count);
        var span = px.Pixels;
        foreach (var q in quads) crops.Add(Preprocess.Crop(span, px.Width, px.Height, q));

        // Every recognizer reads every crop; the reading with the larger Mass wins (see DecodeResult), with one safeguard below.
        var all = new List<Rec>[quads.Count];
        for (int i = 0; i < all.Length; i++) all[i] = new List<Rec>(2);
        foreach (var lang in RecognizersFor(langs))
        {
            var res = GetRecognizer(lang).Recognize(crops);
            for (int i = 0; i < res.Count; i++)
                all[i].Add(new Rec(res[i].Text, res[i].Confidence, res[i].Score, res[i].Mass, lang));
        }
        for (int i = 0; i < best.Length; i++) best[i] = Choose(all[i]);
        for (int i = 0; i < best.Length; i++)
        {
            var fixedText = MixedTokenFix.Apply(best[i].Text);
            if (!ReferenceEquals(fixedText, best[i].Text)) best[i] = best[i] with { Text = fixedText };
            // A line made only of ASCII letters/digits is English whichever recognizer read it (only when English was asked for).
            if (langs.Contains(OcrLanguage.English) && IsLatinOnly(best[i].Text)) best[i] = best[i] with { Lang = OcrLanguage.English };
        }
        return best.ToList();
    }

    /// <summary>Minimum Indic letters/marks in a reading for it to count as "contains a word in an Indic script".</summary>
    const int MinIndicLetters = 2;
    /// <summary>An Indic reading within this fraction of the winner's Mass beats a Latin-only winner (the Latin-only one may have dropped a word).</summary>
    const float IndicPreferenceRatio = 0.85f;

    /// <summary>
    /// Picks the reading with the largest Mass. Safeguard: when the winner is Latin-only but another recognizer found an Indic word
    /// and reads about as well (at least 85% of the winner's Mass), the Indic reading wins, because a recognizer for another script
    /// drops or garbles what it cannot read and still scores well on the Latin part of the line.
    /// </summary>
    static Rec Choose(List<Rec> candidates)
    {
        var best = candidates[0];
        foreach (var c in candidates) if (c.Mass > best.Mass) best = c;
        if (candidates.Count > 1 && IndicLetterCount(best.Text) < MinIndicLetters)
        {
            Rec? alt = null;
            foreach (var c in candidates)
                if (c.Lang != best.Lang && IndicLetterCount(c.Text) >= MinIndicLetters && c.Mass >= IndicPreferenceRatio * best.Mass
                    && (alt == null || c.Mass > alt.Value.Mass))
                    alt = c;
            if (alt != null) return alt.Value;
        }
        return best;
    }

    internal static int IndicLetterCount(string t)
    {
        int n = 0;
        foreach (var ch in t)
        {
            if (ch < 0x0900 || ch > 0x0DFF) continue;
            var cat = char.GetUnicodeCategory(ch);
            if (cat is System.Globalization.UnicodeCategory.OtherLetter or System.Globalization.UnicodeCategory.NonSpacingMark
                or System.Globalization.UnicodeCategory.SpacingCombiningMark) n++;
        }
        return n;
    }

    static List<OcrLine> BuildLines(IReadOnlyList<Rec> results, IReadOnlyList<Pt[]> quads, IReadOnlyList<OcrLanguage> langs, bool keepEmpty)
    {
        var lines = new List<OcrLine>();
        for (int i = 0; i < quads.Count; i++)
        {
            if (!keepEmpty && string.IsNullOrWhiteSpace(results[i].Text)) continue;
            lines.Add(new OcrLine(results[i].Text, Math.Max(0, results[i].Confidence), results[i].Lang, Bounds(quads[i]), ToPoly(quads[i])));
        }
        return lines;
    }

    /// <summary>True when the text has letters or digits and every letter is ASCII (so the line is English, not an Indic script).</summary>
    static bool IsLatinOnly(string t)
    {
        bool any = false;
        foreach (var ch in t)
        {
            if (!char.IsLetterOrDigit(ch)) continue;
            if (ch > 127) return false;
            any = true;
        }
        return any;
    }

    static PointF[] ToPoly(Pt[] q) => q.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();

    static RectangleF Bounds(Pt[] q)
    {
        float x0 = (float)q.Min(p => p.X), x1 = (float)q.Max(p => p.X);
        float y0 = (float)q.Min(p => p.Y), y1 = (float)q.Max(p => p.Y);
        return new RectangleF(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Reads png/jpg/tif/bmp/pdf (PDF rendered at 300 dpi, lower for very large pages) and yields each page with its OCR result.
    /// When the page was turned upright (<see cref="OcrPage.RotationApplied"/> not 0) the yielded image is the turned bitmap, so the
    /// boxes always match it. The caller owns and must dispose each yielded image.
    /// </summary>
    public async IAsyncEnumerable<(SKBitmap Image, OcrPage Page)> RecognizeFileAsync(
        string path, IReadOnlyList<OcrLanguage> languages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Input file not found", path);
        if (!InputFiles.IsSupported(path)) throw new NotSupportedException($"Unsupported file type: {Path.GetExtension(path)}");
        var langs = CheckLangs(languages);
        using var pages = DocumentReader.ReadPages(path).GetEnumerator();
        int index = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int pageIndex = index++;
            var step = await Task.Run(() =>
            {
                if (!pages.MoveNext()) return ((SKBitmap?)null, (OcrPage?)null);
                var bmp = pages.Current.Bitmap;
                try
                {
                    var (page, rotated) = RecognizeOriented(bmp, langs, pageIndex);
                    page = page with { SourceDpi = pages.Current.Dpi };
                    if (rotated == null) return (bmp, page);
                    bmp.Dispose();
                    return (rotated, page);
                }
                catch { bmp.Dispose(); throw; }
            }, ct).ConfigureAwait(false);
            if (step.Item1 == null) yield break;
            yield return (step.Item1, step.Item2!);
        }
    }

    /// <summary>Releases the native sessions. Call only when no recognition is running (see the class remarks).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_loadLock)
        {
            _detector?.Dispose();
            foreach (var r in _recs.Values) r.Dispose();
        }
    }
}
