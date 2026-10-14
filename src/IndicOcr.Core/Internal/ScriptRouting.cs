namespace IndicOcr.Core.Internal;

/// <summary>Decision rules for <see cref="OcrLanguage.Auto"/>: sample selection, page-level script vote and the poor-line test.</summary>
internal static class ScriptRouting
{
    /// <summary>Spread sample: among lines at least 35% as long as the longest, n lines evenly spaced from top to bottom.</summary>
    public static List<int> PickSample(IReadOnlyList<Pt[]> quads, int n)
    {
        var len = quads.Select(q => { var (w, h) = PageOrientation.Sides(q); return Math.Max(w, h); }).ToArray();
        double max = len.Length == 0 ? 0 : len.Max();
        var pool = Enumerable.Range(0, quads.Count).Where(i => len[i] >= 0.35 * max)
            .OrderBy(i => quads[i].Average(p => p.Y)).ToList();
        if (pool.Count <= n) return pool;
        var res = new List<int>(n);
        for (int k = 0; k < n; k++) res.Add(pool[(int)((k + 0.5) * pool.Count / n)]);
        return res;
    }

    // Measured on rendered pages (see AutoLanguageTests): the right recognizer reads a line with mean log-probability per frame
    // (Score) of -0.01 to -0.08, a wrong one of -0.07 to -0.45. Mass alone cannot separate Telugu from Kannada (the Telugu model reads
    // Kannada glyphs with a Mass near the Kannada model's), Score separates them by 0.05 to 0.15. Mass is still used to rule out a
    // recognizer that DROPPED part of the line (a high Score on blank frames).
    /// <summary>A reading is a candidate only if its Mass is at least this fraction of the largest Mass (otherwise it dropped text).</summary>
    public const float EligibleMassRatio = 0.6f;
    /// <summary>Bonus (in Score units) for the page's primary script when a poor line is re-read.</summary>
    public const float PrimaryBonus = 0.02f;
    /// <summary>A sample line votes for a script only when its Score beats the next Indic reading by this much.</summary>
    public const float VoteMargin = 0.03f;
    /// <summary>A line is "poor" (re-read by the other recognizers) when its confidence is under this absolute floor...</summary>
    public const float PoorFloor = 0.80f;
    /// <summary>...or this far under the confidence of the sample lines that voted for the primary script, or its Score this far under theirs.</summary>
    public const float PoorConfidenceDrop = 0.05f, PoorScoreDrop = 0.05f;

    /// <summary>
    /// The reading of one crop that wins among the candidates' readings: drop readings that lost text, prefer one with Indic letters
    /// when the best by Mass is Latin-only (a recognizer for another script reads the Latin part of a mixed line well), then take the
    /// best Score (plus <see cref="PrimaryBonus"/> for <paramref name="primary"/>). Also reports the Score margin to the runner-up.
    /// </summary>
    public static OcrEngine.Rec Pick(IReadOnlyList<OcrEngine.Rec> reads, OcrLanguage? primary, out float margin)
    {
        float maxMass = reads.Max(r => r.Mass);
        var bestByMass = reads.First(r => r.Mass == maxMass);
        var pool = reads.Where(r => r.Mass >= EligibleMassRatio * maxMass).ToList();
        if (OcrEngine.IndicLetterCount(bestByMass.Text) < 2)
        {
            var indic = reads.Where(r => OcrEngine.IndicLetterCount(r.Text) >= 2 && r.Mass >= 0.85f * maxMass).ToList();
            if (indic.Count > 0) pool = indic;
        }
        float Key(OcrEngine.Rec r) => r.Score + (r.Lang == primary ? PrimaryBonus : 0f);
        var ranked = pool.OrderByDescending(Key).ToList();
        margin = ranked.Count > 1 ? Key(ranked[0]) - Key(ranked[1]) : float.PositiveInfinity;
        return ranked[0];
    }

    /// <summary>
    /// The primary recognizer: the script that wins the most sample lines with a clear Score margin. Lines whose winning reading has no
    /// Indic letters (English, numbers) do not vote. Ties, and "no Indic line at all", go to the largest total Score.
    /// </summary>
    public static int PickPrimary(IReadOnlyList<List<OcrEngine.Rec>> sampleReads, IReadOnlyList<OcrLanguage> cands, out List<int> votingLines)
    {
        var votes = new int[cands.Count];
        var total = new double[cands.Count];
        var voters = new List<(int Line, int Cand)>();
        for (int li = 0; li < sampleReads.Count; li++)
        {
            var reads = sampleReads[li];
            for (int c = 0; c < cands.Count; c++) total[c] += reads[c].Score;
            var win = Pick(reads, null, out float margin);
            if (OcrEngine.IndicLetterCount(win.Text) < 2 || margin < VoteMargin) continue;
            int w = cands.ToList().IndexOf(win.Lang);
            votes[w]++;
            voters.Add((li, w));
        }
        int best = 0;
        for (int c = 1; c < cands.Count; c++)
            if (votes[c] > votes[best] || (votes[c] == votes[best] && total[c] > total[best])) best = c;
        votingLines = voters.Where(v => v.Cand == best).Select(v => v.Line).ToList();
        return best;
    }

    /// <summary>
    /// The first recognizer settles the script alone when the sample heads it reads as Indic text average at least this confidence and
    /// Score and none is below the minimum confidence. Measured: the right recognizer averages 0.97 / -0.05 to -0.09 on heads (Hindi
    /// heads score lowest; a head cut in the middle of a glyph can dip to 0.90); Telugu reading Kannada averages 0.88 / -0.10 to -0.12; a wrong script (Hindi, Tamil) reads under 0.7.
    /// A lenient settle is safe because the per-line doubt test (<see cref="Assess"/>) still catches individual lines of another script.
    /// </summary>
    public const float SettleMeanConfidence = 0.95f, SettleMeanScore = -0.12f, SettleMinConfidence = 0.88f;

    public static bool SettlesScript(IReadOnlyList<OcrEngine.Rec> reads)
    {
        // Heads with no Indic letters are English or numbers (any recognizer reads them well) unless they read badly (garbage / empty).
        var rel = reads.Where(r => OcrEngine.IndicLetterCount(r.Text) >= 2 || r.Confidence < PoorFloor).ToList();
        if (rel.Count == 0) return true;
        return rel.Average(r => r.Confidence) >= SettleMeanConfidence && rel.Average(r => r.Score) >= SettleMeanScore
            && rel.All(r => r.Confidence >= SettleMinConfidence && OcrEngine.IndicLetterCount(r.Text) >= 2);
    }

    /// <summary>Confidence and Score of what the primary recognizer reads when it is right (median over the lines that voted for it).</summary>
    public readonly record struct Reference(float Confidence, float Score);

    public static Reference MakeReference(IReadOnlyList<OcrEngine.Rec> primaryReadsOfVoters)
    {
        if (primaryReadsOfVoters.Count == 0) return new Reference(0.9f, -0.1f); // no evidence (an English or empty sample): only the absolute floor matters
        return new Reference(Median(primaryReadsOfVoters.Select(r => r.Confidence)), Median(primaryReadsOfVoters.Select(r => r.Score)));
    }

    static float Median(IEnumerable<float> v)
    {
        var a = v.OrderBy(x => x).ToArray();
        return a[a.Length / 2];
    }

    /// <summary>
    /// How much a line's primary reading needs a second opinion. Strong: probably another script, ask every other recognizer.
    /// Twin: possibly the lookalike script (Telugu and Kannada glyphs are close and the Kannada model reads Telugu with confidence
    /// 0.67 to 0.94), ask only the twin.
    /// </summary>
    public enum Doubt { None, Twin, Strong }

    public const float TwinConfidenceDrop = 0.03f;

    public static OcrLanguage? Twin(OcrLanguage l) => l switch { OcrLanguage.Telugu => OcrLanguage.Kannada, OcrLanguage.Kannada => OcrLanguage.Telugu, _ => null };

    /// <summary>
    /// A Kannada reading that needs no Telugu cross-check: the Kannada model reads Kannada with confidence 0.945 to 1.0 and Score -0.006 to
    /// -0.043 (measured on 60 rendered lines), and Telugu lines with confidence 0.67 to 0.94 and Score -0.048 to -0.22 (the closest Telugu
    /// line reached 0.936 / -0.048). A reading must clear both absolute marks and not trail the page's Kannada reference by more than
    /// <see cref="KannadaSureScoreDrop"/>. About four in five Kannada lines qualify, which is what keeps Kannada pages cheap.
    /// </summary>
    public const float KannadaSureConfidence = 0.95f, KannadaSureScore = -0.04f, KannadaSureScoreDrop = 0.02f;

    public static Doubt Assess(OcrEngine.Rec r, Reference reference)
    {
        if (r.Text.Length == 0) return Doubt.None;
        if (r.Confidence < PoorFloor || r.Confidence < reference.Confidence - PoorConfidenceDrop || r.Score < reference.Score - PoorScoreDrop)
            return Doubt.Strong;
        // The weak Kannada model cannot tell its own glyphs from Telugu by confidence alone: only a very clean reading is trusted.
        if (r.Lang == OcrLanguage.Kannada)
            return r.Confidence >= KannadaSureConfidence && r.Score >= KannadaSureScore && r.Score >= reference.Score - KannadaSureScoreDrop
                ? Doubt.None : Doubt.Twin;
        if (r.Confidence < reference.Confidence - TwinConfidenceDrop) return Doubt.Twin;
        return Doubt.None;
    }
}
