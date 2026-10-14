using System.Text;

namespace IndicOcr.Core.Internal;

/// <summary>PaddleOCR CTC character table: index 0 = blank, i+1 = dict line i, last = space.</summary>
internal sealed class CharTable
{
    readonly string[] _chars;
    public int ClassCount => _chars.Length;

    CharTable(string[] chars) { _chars = chars; }

    public static CharTable FromLines(IEnumerable<string> dictLines)
    {
        var list = new List<string> { "" }; // blank
        list.AddRange(dictLines);
        list.Add(" ");
        return new CharTable(list.ToArray());
    }

    public static CharTable Load(string path)
    {
        var lines = new List<string>();
        using var r = new StreamReader(path, new UTF8Encoding(false));
        string? line;
        while ((line = r.ReadLine()) != null) lines.Add(line.Trim('\r', '\n'));
        return FromLines(lines);
    }

    public string this[int i] => _chars[i];

    /// <summary>Throws if the model's class count does not match the dictionary (dict + blank + space).</summary>
    public void Validate(int modelClasses, string what = "recognizer")
    {
        if (modelClasses != ClassCount)
            throw new InvalidDataException(
                $"{what}: model outputs {modelClasses} classes but the dictionary gives {ClassCount} (dict lines + 2).");
    }
}

/// <summary>
/// Result of a greedy CTC decode.
/// Confidence = mean max-probability of the kept characters (what the UI shows).
/// Mass = sum over the kept characters of (2p - 1): about "characters read correctly minus characters invented". Recognizers are
/// compared by Mass, because (unlike a mean) a recognizer that DROPS part of the line, for example a script it cannot read,
/// scores lower than one that reads it, and a garbled hallucination (p near 0.5) earns almost nothing.
/// Score = mean over ALL frames (blanks included) of log(max-probability); used only to judge whether a line reads well.
/// </summary>
internal readonly record struct DecodeResult(string Text, float Confidence, float Score, float Mass)
{
    public void Deconstruct(out string text, out float confidence) { text = Text; confidence = Confidence; }
}

internal static class CtcDecoder
{
    /// <summary>
    /// Greedy CTC decode of one sequence. probs: [T*C] softmax. Collapses repeats, drops blanks.
    /// Confidence = mean of the max probability of the kept characters (0 if none).
    /// </summary>
    public static DecodeResult Decode(ReadOnlySpan<float> probs, int t, int c, CharTable table)
    {
        var sb = new StringBuilder();
        double conf = 0, logSum = 0, mass = 0;
        int kept = 0, prev = 0;
        for (int i = 0; i < t; i++)
        {
            var row = probs.Slice(i * c, c);
            int best = 0;
            float bp = row[0];
            for (int k = 1; k < c; k++)
                if (row[k] > bp) { bp = row[k]; best = k; }
            logSum += Math.Log(Math.Max(bp, 1e-6f));
            if (best != 0 && best != prev)
            {
                sb.Append(table[best]);
                conf += bp;
                mass += 2 * bp - 1;
                kept++;
            }
            prev = best;
        }
        return new DecodeResult(sb.ToString(), kept == 0 ? 0f : (float)(conf / kept), t == 0 ? 0f : (float)(logSum / t), (float)mass);
    }
}
