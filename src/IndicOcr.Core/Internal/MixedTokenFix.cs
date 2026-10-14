using System.Text;

namespace IndicOcr.Core.Internal;

/// <summary>
/// Narrow post-fix for identifiers (survey numbers "Sy.No.12/A", PAN, vehicle numbers, "Rs.5000") read by an Indic recognizer:
/// inside a whitespace-delimited token that has at least two ASCII letters and NO other non-ASCII character, an isolated
/// zero-like Indic glyph is mapped back to Latin. Zero digits of Telugu/Kannada/Tamil/Devanagari become '0'. An anusvara
/// (which the recognizers produce for a round 'o' or '0', but also as a stray mark) is mapped only when the context is unambiguous:
/// '0' when BOTH neighbours are digits; 'o' ('O' in an upper-case run) when neither neighbour is a digit and the previous one is a
/// Latin letter (the next one a letter, punctuation or the token end). Anything else, e.g. a mark after the last digit of "Rs.5000"
/// or between letters and digits in a GSTIN, is left unchanged rather than inventing a character.
/// Tokens that contain any other Indic character are left alone, so real Indic words are never touched.
/// </summary>
internal static class MixedTokenFix
{
    static bool IsZeroDigit(char c) => c is '०' or '௦' or '౦' or '೦';
    static bool IsAnusvara(char c) => c is 'ం' or 'ಂ' or 'ं' or 'ஂ';
    static bool IsSuspect(char c) => IsZeroDigit(c) || IsAnusvara(c);

    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        bool any = false;
        foreach (var c in text) if (IsSuspect(c)) { any = true; break; }
        if (!any) return text;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        bool changed = false;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { sb.Append(text[i++]); continue; }
            int j = i;
            while (j < text.Length && !char.IsWhiteSpace(text[j])) j++;
            var tok = text.AsSpan(i, j - i);
            var fixedTok = FixToken(tok);
            if (fixedTok != null) { sb.Append(fixedTok); changed = true; }
            else sb.Append(tok);
            i = j;
        }
        return changed ? sb.ToString() : text;
    }

    /// <summary>The repaired token, or null when the token is not eligible or nothing changed.</summary>
    static string? FixToken(ReadOnlySpan<char> t)
    {
        int latinLetters = 0;
        bool suspect = false;
        foreach (var c in t)
        {
            if (c < 128) { if (char.IsAsciiLetter(c)) latinLetters++; }
            else if (IsSuspect(c)) suspect = true;
            else return null; // any other non-ASCII character: a real Indic word, leave it
        }
        if (!suspect || latinLetters < 2) return null;

        var r = t.ToArray();
        for (int k = 0; k < r.Length; k++)
        {
            char c = r[k];
            if (IsZeroDigit(c)) { r[k] = '0'; continue; }
            if (!IsAnusvara(c)) continue;
            char prev = k > 0 ? r[k - 1] : '\0', next = k + 1 < r.Length ? r[k + 1] : '\0';
            if (char.IsAsciiDigit(prev) && char.IsAsciiDigit(next)) r[k] = '0';
            else if (char.IsAsciiDigit(prev) || char.IsAsciiDigit(next) || !char.IsAsciiLetter(prev)) continue;
            else if (char.IsAsciiLetterUpper(prev) && (next == '\0' || char.IsAsciiLetterUpper(next))) r[k] = 'O';
            else r[k] = 'o';
        }
        return new string(r);
    }
}
