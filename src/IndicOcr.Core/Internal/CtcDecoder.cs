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

internal static class CtcDecoder
{
    /// <summary>
    /// Greedy CTC decode of one sequence. probs: [T*C] softmax. Collapses repeats, drops blanks.
    /// Confidence = mean of the max probability of the kept characters (0 if none).
    /// </summary>
    public static (string Text, float Confidence) Decode(ReadOnlySpan<float> probs, int t, int c, CharTable table)
    {
        var sb = new StringBuilder();
        double conf = 0;
        int kept = 0, prev = 0;
        for (int i = 0; i < t; i++)
        {
            var row = probs.Slice(i * c, c);
            int best = 0;
            float bp = row[0];
            for (int k = 1; k < c; k++)
                if (row[k] > bp) { bp = row[k]; best = k; }
            if (best != 0 && best != prev)
            {
                sb.Append(table[best]);
                conf += bp;
                kept++;
            }
            prev = best;
        }
        return (sb.ToString(), kept == 0 ? 0f : (float)(conf / kept));
    }
}
