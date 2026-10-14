using IndicOcr.Core.Internal;

namespace IndicOcr.Core;

// Automatic script detection per line (OcrLanguage.Auto). Strategy, in order of cost:
//  1. A few sample lines, spread over the page, are read by EVERY candidate Indic recognizer. The script that wins them (by Mass,
//     with a margin and a handicap for the weak Kannada model) becomes the page's PRIMARY recognizer.
//  2. Every other line is read by the primary recognizer only (this is the whole cost of a single-script page, plus the sample).
//     When no sampled line is in an Indic script (English, numbers) the primary is the English recognizer if installed.
//  3. A line the primary reads poorly (low confidence: typically another script) is also read by the remaining candidates and the
//     best reading by Score wins. This is what finds a Hindi paragraph in a Telugu page, one line at a time. Telugu and Kannada are
//     lookalikes: a Telugu line the primary reads a bit worse is also read by Kannada and vice versa, except that a Kannada reading
//     clean enough to be certain (ScriptRouting.Assess) is not cross-checked, which keeps a Kannada page at about 1.5 to 1.7x the cost of naming it (it was 3.6x).
//  4. Latin-only results are labelled English; MixedTokenFix is applied as in explicit mode.
// The result of a page depends on the page alone (no engine state, nothing from other threads or earlier calls). The only carry-over is
// the script hint of one document (<see cref="AutoContext"/>), which RecognizeFileAsync keeps across the pages of that file.
public sealed partial class OcrEngine
{
    /// <summary>
    /// Per-document state of Auto: the script that won the previous page of the same file (tried first on the next page, which saves the
    /// full vote). Created per RecognizeFileAsync call, never shared between files or threads; single-page calls pass none.
    /// </summary>
    internal sealed class AutoContext
    {
        public OcrLanguage? Hint;
    }

    bool HasModel(OcrLanguage l)
    {
        var p = ModelPrefix(l);
        return File.Exists(Path.Combine(_dir, $"rec_{p}.onnx")) && File.Exists(Path.Combine(_dir, $"dict_{p}.txt"));
    }

    static bool IsAuto(IReadOnlyList<OcrLanguage> langs) => langs.Contains(OcrLanguage.Auto);

    static readonly OcrLanguage[] IndicLanguages = { OcrLanguage.Telugu, OcrLanguage.Hindi, OcrLanguage.Tamil, OcrLanguage.Kannada };

    /// <summary>The Indic recognizers Auto chooses between: those listed with Auto, else every one whose model files exist.</summary>
    IReadOnlyList<OcrLanguage> AutoCandidates(IReadOnlyList<OcrLanguage> langs)
    {
        var listed = IndicLanguages.Where(langs.Contains).ToList();
        var pool = listed.Count > 0 ? listed : IndicLanguages.ToList();
        var have = pool.Where(HasModel).ToList();
        if (have.Count == 0)
            throw new FileNotFoundException($"Automatic language detection needs at least one Indic model (te, hi, ta, kn) in {_dir}.");
        return have;
    }

    List<Rec> RecognizeQuadsAuto(PixelImage px, List<Pt[]> quads, IReadOnlyList<OcrLanguage> langs, bool rotateTall, AutoContext? ctx)
    {
        var cands = AutoCandidates(langs);
        // English is an extra reader, never a voter: it reads the page only when no line of the sample is in an Indic script.
        var readers = cands.ToList();
        int enIdx = -1;
        if (cands.Count > 1 && HasModel(OcrLanguage.English)) { enIdx = readers.Count; readers.Add(OcrLanguage.English); }
        int n = quads.Count;
        var best = new Rec[n];
        if (n == 0) return best.ToList();
        var span = px.Pixels;
        var crops = new List<Preprocess.LineCrop>(n);
        foreach (var q in quads) crops.Add(Preprocess.Crop(span, px.Width, px.Height, q, rotateTall));

        // reads[i][c] = reading of line i by candidate c (null = not run).
        var reads = new Rec?[n][];
        for (int i = 0; i < n; i++) reads[i] = new Rec?[readers.Count];
        void RunOn(int c, IReadOnlyList<int> lines)
        {
            var todo = lines.Where(i => reads[i][c] == null).ToList();
            if (todo.Count == 0) return;
            var res = GetRecognizer(readers[c]).Recognize(todo.Select(i => crops[i]).ToList());
            for (int k = 0; k < todo.Count; k++)
                reads[todo[k]][c] = new Rec(res[k].Text, res[k].Confidence, res[k].Score, res[k].Mass, readers[c]);
        }

        // 1) script ID: the left end of a spread sample of lines is read first by the likely recognizer (the one that won the previous
        //    page of the same file; Telugu, the first in the fixed order, without a hint). When it reads the sample with high confidence
        //    the script is settled and nothing else runs (a wrong-script reading is never that confident; Telugu reading Kannada is the
        //    closest call and the thresholds were measured against it). Kannada is never settled this way: its model reads Telugu with
        //    confidence too. Otherwise the sample is also read by the other candidates and the scripts vote.
        int primary = 0;
        List<int> voters = new();
        if (cands.Count > 1)
        {
            var sample = ScriptRouting.PickSample(quads, ScriptSampleSize);
            var heads = sample.Select(i => Preprocess.Head(crops[i], ScriptSampleWidth)).ToList();
            var sampleReads = new List<Rec>[sample.Count];
            for (int k = 0; k < sample.Count; k++) sampleReads[k] = new List<Rec>(cands.Count);
            int first = ctx?.Hint is { } hint ? Math.Max(0, cands.ToList().IndexOf(hint)) : 0;
            var firstRes = GetRecognizer(cands[first]).Recognize(heads);
            var firstReads = firstRes.Select(r => new Rec(r.Text, r.Confidence, r.Score, r.Mass, cands[first])).ToList();
            if (cands[first] != OcrLanguage.Kannada && ScriptRouting.SettlesScript(firstReads))
            {
                primary = first;
                voters = Enumerable.Range(0, sample.Count).Where(k => IndicLetterCount(firstReads[k].Text) >= 2).Select(k => sample[k]).ToList();
            }
            else
            {
                for (int c = 0; c < cands.Count; c++)
                {
                    var reads2 = c == first ? firstReads : GetRecognizer(cands[c]).Recognize(heads)
                        .Select(r => new Rec(r.Text, r.Confidence, r.Score, r.Mass, cands[c])).ToList();
                    for (int k = 0; k < sample.Count; k++) sampleReads[k].Add(reads2[k]);
                }
                primary = ScriptRouting.PickPrimary(sampleReads, cands, out var voterIdx);
                voters = voterIdx.Select(v => sample[v]).ToList();
            }
            // No line of the sample is in an Indic script (English, numbers): the English recognizer reads the page when it is installed,
            // else the first candidate in the fixed order. Which recognizer happened to read the sample first must not matter.
            if (voters.Count == 0) primary = enIdx >= 0 ? enIdx : 0;
            else if (ctx != null) ctx.Hint = cands[primary];
        }

        // 2) the primary reads everything
        RunOn(primary, Enumerable.Range(0, n).ToList());
        var reference = ScriptRouting.MakeReference(voters.Select(i => reads[i][primary]!.Value).ToList());

        // 3) doubtful lines get a second opinion: strong doubt from every other recognizer, twin doubt (Telugu <-> Kannada) from the twin
        var strong = new List<int>(); var twin = new List<int>();
        if (cands.Count > 1)
            for (int i = 0; i < n; i++)
                switch (ScriptRouting.Assess(reads[i][primary]!.Value, reference))
                {
                    case ScriptRouting.Doubt.Strong: strong.Add(i); break;
                    case ScriptRouting.Doubt.Twin: twin.Add(i); break;
                }
        int twinIdx = primary < cands.Count && ScriptRouting.Twin(cands[primary]) is { } tw ? cands.ToList().IndexOf(tw) : -1;
        for (int c = 0; c < cands.Count; c++)  // the English reader is never a second opinion
        {
            if (c == primary) continue;
            RunOn(c, strong);
            if (c == twinIdx) RunOn(c, twin);
        }
        var doubtful = strong.Concat(twin).ToHashSet();
        for (int i = 0; i < n; i++)
            best[i] = doubtful.Contains(i)
                ? ScriptRouting.Pick(reads[i].Where(r => r != null).Select(r => r!.Value).ToList(), readers[primary], out _)
                : reads[i][primary]!.Value;

        for (int i = 0; i < n; i++)
        {
            var fixedText = MixedTokenFix.Apply(best[i].Text);
            if (!ReferenceEquals(fixedText, best[i].Text)) best[i] = best[i] with { Text = fixedText };
            if (IsLatinOnly(best[i].Text)) best[i] = best[i] with { Lang = OcrLanguage.English };
        }
        return best.ToList();
    }

    const int ScriptSampleSize = 6;
    /// <summary>Sample crops are cut to this width (about 14 characters): enough to tell the scripts apart at a quarter of the cost.</summary>
    const int ScriptSampleWidth = 384;

    /// <summary>Languages present in the lines, most characters first. Indic needs 2 letters of the script, English 2 ASCII letters.</summary>
    internal static IReadOnlyList<OcrLanguage> Detected(IReadOnlyList<OcrLine> lines)
    {
        var count = new Dictionary<OcrLanguage, int>();
        foreach (var l in lines)
        {
            int n = l.Language == OcrLanguage.English ? l.Text.Count(char.IsAsciiLetter) : IndicLetterCount(l.Text);
            if (n < 2) continue;
            count[l.Language] = count.GetValueOrDefault(l.Language) + n;
        }
        return count.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }
}
