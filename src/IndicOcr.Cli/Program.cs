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

        Output files are written next to each input (or under --out-dir, keeping sub-folders of a folder input):
          <name>.txt, <name>.docx, <name>.ocr.pdf   (the searchable PDF never replaces the input)
        If two inputs in one folder share a base name (multi.tif + multi.pdf), the source extension is added:
          multi.tif.txt, multi.pdf.txt, multi.tif.ocr.pdf, ...
        The command refuses to run if an output path equals an input path or another output.
        A file that fails is reported as "FAILED <file>: <reason>" and the rest still run; exit code 1 if any failed, 2 for bad arguments.

        Languages: en, te (Telugu), hi (Hindi/Marathi, Devanagari; "mr" is an alias), ta (Tamil), kn (Kannada, beta: older model, expect many errors); join with +.
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
        catch (OperationCanceledException) { return 130; }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
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

    static int ParseInt(Dictionary<string, string> o, string key, int min, int def)
    {
        if (!o.TryGetValue(key, out var v)) return def;
        if (!int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n) || n < min)
            throw new ArgumentException($"--{key} must be an integer >= {min} (got '{v}').");
        return n;
    }

    static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Output file name stem: "name", or "name.ext" when another input in the same folder has the same base name.</summary>
    static string OutputStem(string file, bool disambiguate)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        return disambiguate ? name + "." + Path.GetExtension(file).TrimStart('.').ToLowerInvariant() : name;
    }

    static string OutputName(string stem, string format) => format == "pdf" ? stem + ".ocr.pdf" : stem + "." + format;

    static int Ocr(List<string> positional, Dictionary<string, string> o, string models)
    {
        if (positional.Count != 1) throw new ArgumentException("ocr needs exactly one file or folder.");
        if (string.IsNullOrEmpty(models)) throw new ArgumentException("--models DIR (or INDICOCR_MODELS) is required.");
        var langs = ParseLanguages(o.GetValueOrDefault("lang") ?? "");
        var formats = (o.GetValueOrDefault("format") ?? "txt").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.ToLowerInvariant()).Distinct().ToList();
        if (formats.Count == 0) throw new ArgumentException("--format needs at least one of txt, docx, pdf.");
        foreach (var f in formats) if (f is not ("txt" or "docx" or "pdf")) throw new ArgumentException($"Unknown format '{f}'. Use txt, docx, pdf.");
        string? outDir = o.GetValueOrDefault("out-dir");
        string? fonts = o.GetValueOrDefault("fonts");
        bool recursive = o.ContainsKey("recursive");
        var eo = new OcrEngineOptions
        {
            MaxSideLength = ParseInt(o, "max-side", 32, new OcrEngineOptions().MaxSideLength),
            IntraOpThreads = ParseInt(o, "threads", 0, 0),
        };

        var files = InputFiles.Enumerate(positional[0], recursive).ToList();
        if (files.Count == 0) { Console.Error.WriteLine("No supported files found (png, jpg, tif, bmp, pdf)."); return 1; }

        var available = OcrEngine.AvailableLanguages(models);
        var missing = langs.Where(l => !available.Contains(l)).ToList();
        if (missing.Count > 0) throw new ArgumentException("Models not found for: " + string.Join(", ", missing.Select(Code)));

        // Plan every output path up front and refuse anything that would overwrite an input or another output.
        string root = Directory.Exists(positional[0]) ? Path.GetFullPath(positional[0]) : Path.GetDirectoryName(Path.GetFullPath(positional[0]))!;
        var plans = new List<(string File, Dictionary<string, string> Outputs)>();
        var inputSet = new HashSet<string>(files.Select(Path.GetFullPath), PathComparer);
        var sameDirBase = files.GroupBy(f => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(f))!, Path.GetFileNameWithoutExtension(f)), PathComparer)
            .Where(g => g.Count() > 1).SelectMany(g => g).Select(Path.GetFullPath).ToHashSet(PathComparer);
        var claimed = new Dictionary<string, string>(PathComparer);
        foreach (var file in files)
        {
            string full = Path.GetFullPath(file);
            string srcDir = Path.GetDirectoryName(full)!;
            string dir = outDir == null ? srcDir : Path.Combine(Path.GetFullPath(outDir), Path.GetRelativePath(root, srcDir));
            string stem = OutputStem(full, sameDirBase.Contains(full));
            var outs = new Dictionary<string, string>();
            foreach (var fmt in formats)
            {
                string target = Path.GetFullPath(Path.Combine(dir, OutputName(stem, fmt)));
                if (inputSet.Contains(target))
                    throw new ArgumentException($"Refusing to write '{target}': it is also an input file.");
                if (claimed.TryGetValue(target, out var other))
                    throw new ArgumentException($"Output '{target}' would be written by both '{other}' and '{file}'.");
                claimed[target] = file;
                outs[fmt] = target;
            }
            plans.Add((file, outs));
        }

        using var engine = new OcrEngine(models, eo);
        var total = Stopwatch.StartNew();
        int pageCount = 0, failed = 0;
        foreach (var (file, outs) in plans)
        {
            var written = new List<string>();
            try
            {
                foreach (var p in outs.Values) Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                var pages = new List<OcrPage>();
                var stream = Stream(engine, file, langs, pages);
                if (outs.TryGetValue("pdf", out var pdf))
                {
                    written.Add(pdf);
                    SearchablePdfExporter.Write(stream, pdf, fonts);
                }
                else
                    foreach (var (img, _) in stream) { }
                if (outs.TryGetValue("txt", out var txt)) { written.Add(txt); TextExporter.Write(pages, txt); }
                if (outs.TryGetValue("docx", out var docx)) { written.Add(docx); DocxExporter.Write(pages, docx); }
                pageCount += pages.Count;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                failed++;
                foreach (var w in written) { try { File.Delete(w); } catch { /* best effort */ } }
                // Only the exception type and message are printed, never document text.
                Console.Error.WriteLine($"FAILED {file}: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}");
            }
        }
        total.Stop();
        Console.WriteLine($"Done: {files.Count - failed} of {files.Count} file(s), {pageCount} page(s) in {total.Elapsed.TotalSeconds:F2}s" + (failed > 0 ? $", {failed} FAILED" : ""));
        return failed > 0 ? 1 : 0;
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
