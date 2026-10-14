using System.Diagnostics;
using IndicOcr.Core;
using SkiaSharp;

namespace IndicOcr.Cli;

static class Program
{
    const string Usage = """
        indicocr - offline Indic OCR

        Usage:
          indicocr ocr <file-or-folder> --models DIR --lang te+en [--out-dir DIR] [--format txt,docx,pdf] [--recursive]
                       [--fonts DIR] [--max-side N] [--threads N]
          indicocr langs --models DIR

        Languages: en, te (Telugu), hi (Hindi/Marathi, Devanagari; "mr" is an alias), ta (Tamil), kn (Kannada); join with +.
        te/hi/ta + en runs only the Indic recognizer (its dictionary includes A-Z, a-z, 0-9).
        The models folder defaults to the INDICOCR_MODELS environment variable.
        """;

    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
            var opts = ParseOptions(args.Skip(1).ToArray(), out var positional);
            string models = opts.GetValueOrDefault("models") ?? Environment.GetEnvironmentVariable("INDICOCR_MODELS") ?? "";
            switch (args[0])
            {
                case "langs":
                    return Langs(models);
                case "ocr":
                    return Ocr(positional, opts, models);
                default:
                    Console.Error.WriteLine($"Unknown command '{args[0]}'.\n{Usage}");
                    return 2;
            }
        }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Error: " + e.Message);
            return 1;
        }
    }

    static readonly HashSet<string> Flags = new() { "recursive" };

    static Dictionary<string, string> ParseOptions(string[] a, out List<string> positional)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        positional = new();
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--", StringComparison.Ordinal))
            {
                string k = a[i][2..];
                if (Flags.Contains(k)) d[k] = "true";
                else
                {
                    if (i + 1 >= a.Length) throw new ArgumentException($"Option --{k} needs a value.");
                    d[k] = a[++i];
                }
            }
            else positional.Add(a[i]);
        }
        return d;
    }

    static int Langs(string models)
    {
        if (string.IsNullOrEmpty(models)) throw new ArgumentException("--models DIR (or INDICOCR_MODELS) is required.");
        var langs = OcrEngine.AvailableLanguages(models);
        if (langs.Count == 0) { Console.Error.WriteLine("No complete models found in " + models); return 1; }
        foreach (var l in langs) Console.WriteLine($"{Code(l)}\t{l}");
        return 0;
    }

    static string Code(OcrLanguage l) => l switch
    {
        OcrLanguage.English => "en", OcrLanguage.Telugu => "te", OcrLanguage.Hindi => "hi",
        OcrLanguage.Tamil => "ta", OcrLanguage.Kannada => "kn", _ => l.ToString(),
    };

    internal static List<OcrLanguage> ParseLanguages(string spec)
    {
        var res = new List<OcrLanguage>();
        foreach (var part in spec.Split('+', ',', ';').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0))
        {
            res.Add(part switch
            {
                "en" or "eng" or "english" => OcrLanguage.English,
                "te" or "tel" or "telugu" => OcrLanguage.Telugu,
                "hi" or "hin" or "mr" or "mar" or "hindi" or "marathi" or "devanagari" => OcrLanguage.Hindi,
                "ta" or "tam" or "tamil" => OcrLanguage.Tamil,
                "kn" or "ka" or "kan" or "kannada" => OcrLanguage.Kannada,
                _ => throw new ArgumentException($"Unknown language '{part}'. Use en, te, hi, ta, kn."),
            });
        }
        if (res.Count == 0) throw new ArgumentException("--lang is required, e.g. --lang te+en.");
        return res.Distinct().ToList();
    }

    static int Ocr(List<string> positional, Dictionary<string, string> o, string models)
    {
        if (positional.Count != 1) throw new ArgumentException("ocr needs exactly one file or folder.");
        if (string.IsNullOrEmpty(models)) throw new ArgumentException("--models DIR (or INDICOCR_MODELS) is required.");
        var langs = ParseLanguages(o.GetValueOrDefault("lang") ?? "");
        var formats = (o.GetValueOrDefault("format") ?? "txt").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.ToLowerInvariant()).Distinct().ToList();
        foreach (var f in formats) if (f is not ("txt" or "docx" or "pdf")) throw new ArgumentException($"Unknown format '{f}'. Use txt, docx, pdf.");
        string? outDir = o.GetValueOrDefault("out-dir");
        string? fonts = o.GetValueOrDefault("fonts");
        bool recursive = o.ContainsKey("recursive");
        var eo = new OcrEngineOptions
        {
            MaxSideLength = o.TryGetValue("max-side", out var ms) ? int.Parse(ms) : new OcrEngineOptions().MaxSideLength,
            IntraOpThreads = o.TryGetValue("threads", out var th) ? int.Parse(th) : 0,
        };

        var files = InputFiles.Enumerate(positional[0], recursive).ToList();
        if (files.Count == 0) { Console.Error.WriteLine("No supported files found (png, jpg, tif, bmp, pdf)."); return 1; }

        var available = OcrEngine.AvailableLanguages(models);
        var missing = langs.Where(l => !available.Contains(l)).ToList();
        if (missing.Count > 0) throw new ArgumentException("Models not found for: " + string.Join(", ", missing.Select(Code)));

        using var engine = new OcrEngine(models, eo);
        var total = Stopwatch.StartNew();
        int pageCount = 0;
        foreach (var file in files)
        {
            string baseName = Path.GetFileNameWithoutExtension(file);
            string dir = outDir ?? Path.GetDirectoryName(Path.GetFullPath(file))!;
            Directory.CreateDirectory(dir);
            var pages = new List<OcrPage>();
            var stream = Stream(engine, file, langs, pages);
            if (formats.Contains("pdf"))
                SearchablePdfExporter.Write(stream, Path.Combine(dir, baseName + ".pdf"), fonts);
            else
                foreach (var (img, _) in stream) { }
            if (formats.Contains("txt")) TextExporter.Write(pages, Path.Combine(dir, baseName + ".txt"));
            if (formats.Contains("docx")) DocxExporter.Write(pages, Path.Combine(dir, baseName + ".docx"));
            pageCount += pages.Count;
        }
        total.Stop();
        Console.WriteLine($"Done: {files.Count} file(s), {pageCount} page(s) in {total.Elapsed.TotalSeconds:F2}s");
        return 0;
    }

    /// <summary>Synchronous lazy view of the async OCR stream; disposes each page image once the consumer moves on.</summary>
    static IEnumerable<(SKBitmap Image, OcrPage Page)> Stream(OcrEngine engine, string file, IReadOnlyList<OcrLanguage> langs, List<OcrPage> sink)
    {
        var e = engine.RecognizeFileAsync(file, langs).GetAsyncEnumerator();
        try
        {
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var (img, page) = e.Current;
                sink.Add(page);
                Console.WriteLine($"{Path.GetFileName(file)} page {page.PageIndex + 1}: {page.Elapsed.TotalSeconds:F2}s, {page.Lines.Count} lines");
                try { yield return (img, page); }
                finally { img.Dispose(); }
            }
        }
        finally { e.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
