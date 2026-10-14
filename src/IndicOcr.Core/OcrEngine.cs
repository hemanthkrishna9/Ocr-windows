using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using IndicOcr.Core.Internal;
using SkiaSharp;

namespace IndicOcr.Core;

/// <summary>
/// PaddleOCR (PP-OCRv5 mobile; Kannada PP-OCRv3) pipeline on ONNX Runtime: DB detection, line crop, CTC recognition per language.
/// Thread-safe. Recognizer sessions are loaded lazily on first use and cached by language.
/// </summary>
public sealed class OcrEngine : IDisposable
{
    const string DetFile = "det.onnx";

    readonly string _dir;
    readonly OcrEngineOptions _opt;
    readonly Lazy<Detector> _det;
    readonly ConcurrentDictionary<OcrLanguage, Lazy<Recognizer>> _recs = new();
    bool _disposed;

    public OcrEngine(string modelsDirectory, OcrEngineOptions? options = null)
    {
        if (!Directory.Exists(modelsDirectory))
            throw new DirectoryNotFoundException($"Models directory not found: {modelsDirectory}");
        _dir = modelsDirectory;
        _opt = options ?? new OcrEngineOptions();
        string detPath = Path.Combine(_dir, DetFile);
        if (!File.Exists(detPath)) throw new FileNotFoundException("Detector model missing", detPath);
        _det = new Lazy<Detector>(() => new Detector(detPath, _opt), LazyThreadSafetyMode.ExecutionAndPublication);
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

    Recognizer GetRecognizer(OcrLanguage l) =>
        _recs.GetOrAdd(l, lang => new Lazy<Recognizer>(() =>
        {
            var p = ModelPrefix(lang);
            var onnx = Path.Combine(_dir, $"rec_{p}.onnx");
            var dict = Path.Combine(_dir, $"dict_{p}.txt");
            if (!File.Exists(onnx) || !File.Exists(dict))
                throw new FileNotFoundException($"Model files for {lang} not found in {_dir}", onnx);
            return new Recognizer(onnx, dict, _opt);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    static IReadOnlyList<OcrLanguage> CheckLangs(IReadOnlyList<OcrLanguage> languages)
    {
        if (languages == null || languages.Count == 0) throw new ArgumentException("At least one language is required.", nameof(languages));
        return languages.Distinct().ToList();
    }

    public OcrPage Recognize(SKBitmap image, IReadOnlyList<OcrLanguage> languages, int pageIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);
        var langs = CheckLangs(languages);
        var sw = Stopwatch.StartNew();
        using var px = PixelImage.From(image);

        var boxes = _det.Value.Detect(px);
        var ordered = LineOrdering.Order(boxes, b => Bounds(b.Box));
        var lines = RecognizeBoxes(px, ordered.Select(b => b.Box).ToList(), langs);
        sw.Stop();
        return new OcrPage(pageIndex, image.Width, image.Height, lines, sw.Elapsed);
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
        var lines = RecognizeBoxes(px, new List<Pt[]> { quad }, langs, keepEmpty: true);
        return lines.Count > 0
            ? lines[0] with { Box = new RectangleF(x0, y0, x1 - x0, y1 - y0) }
            : new OcrLine("", 0f, langs[0], new RectangleF(x0, y0, x1 - x0, y1 - y0), ToPoly(quad));
    }

    List<OcrLine> RecognizeBoxes(PixelImage px, List<Pt[]> quads, IReadOnlyList<OcrLanguage> langs, bool keepEmpty = false)
    {
        if (quads.Count == 0) return new();
        var crops = new List<Preprocess.LineCrop>(quads.Count);
        var span = px.Pixels;
        foreach (var q in quads) crops.Add(Preprocess.Crop(span, px.Width, px.Height, q));

        var best = new (string Text, float Conf, OcrLanguage Lang)[quads.Count];
        for (int i = 0; i < best.Length; i++) best[i] = ("", -1f, langs[0]);

        // Speed rule: Telugu/Hindi/Tamil (PP-OCRv5) + English -> run only the Indic recognizer; its dictionary has A-Z a-z 0-9.
        var run = langs;
        OcrLanguage? indicOnly = null;
        if (_opt.EnglishViaIndicRecognizer && langs.Count == 2 && langs.Contains(OcrLanguage.English))
        {
            var other = langs.First(l => l != OcrLanguage.English);
            if (other is OcrLanguage.Telugu or OcrLanguage.Hindi or OcrLanguage.Tamil) { run = new[] { other }; indicOnly = other; }
        }
        foreach (var lang in run)
        {
            var res = GetRecognizer(lang).Recognize(crops);
            for (int i = 0; i < res.Count; i++)
                if (res[i].Confidence > best[i].Conf || (best[i].Conf < 0))
                    best[i] = (res[i].Text, res[i].Confidence, lang);
        }
        if (indicOnly != null)
            for (int i = 0; i < best.Length; i++)
                if (IsLatinOnly(best[i].Text)) best[i].Lang = OcrLanguage.English;
        var lines = new List<OcrLine>();
        for (int i = 0; i < quads.Count; i++)
        {
            if (!keepEmpty && string.IsNullOrWhiteSpace(best[i].Text)) continue;
            lines.Add(new OcrLine(best[i].Text, Math.Max(0, best[i].Conf), best[i].Lang, Bounds(quads[i]), ToPoly(quads[i])));
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
    /// Reads png/jpg/tif/bmp/pdf (PDF rendered at 300 dpi) and yields each page with its OCR result.
    /// The caller owns and must dispose each yielded image.
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
                var bmp = pages.Current;
                try { return (bmp, Recognize(bmp, langs, pageIndex)); }
                catch { bmp.Dispose(); throw; }
            }, ct).ConfigureAwait(false);
            if (step.Item1 == null) yield break;
            yield return (step.Item1, step.Item2!);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_det.IsValueCreated) _det.Value.Dispose();
        foreach (var r in _recs.Values) if (r.IsValueCreated) r.Value.Dispose();
    }
}
