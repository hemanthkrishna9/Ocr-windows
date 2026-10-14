using System.Text;
using System.Text.RegularExpressions;

namespace IndicOcr.Core.Internal;

/// <summary>A match inside one (virtual) line's text. Indexes are UTF-16 offsets into the ORIGINAL text. <paramref name="Verified"/> is false when the match passed on keyword or layout evidence only (checksum failed or OCR-corrected characters).</summary>
internal readonly record struct PiiMatch(PiiKind Kind, int Start, int Length, bool Verified = true);

/// <summary>
/// Finds Aadhaar, VID, PAN, phone and email patterns in one line of text (which may be several OCR lines joined). Works on a
/// digit-normalised copy of the line (Telugu, Devanagari, Tamil, Kannada and other Unicode decimal digits become ASCII) and, near a
/// keyword, on a copy where typical OCR letter/digit confusions are repaired. The copies have the same length, so spans map straight
/// back. The matched text never leaves this class except as spans.
/// </summary>
internal static class PiiDetector
{
    // Separators between digit groups: 1-3 spaces, or one dash (hyphen, en/em dash...) or dot with an optional space on each side.
    const string Sep = @"(?:[ \u00A0]{1,3}|[ \u00A0]?[\-\u2010-\u2015\u2212.~|\u00B7][ \u00A0]?)";
    static readonly Regex DigitChain = new($"[0-9]+(?:{Sep}[0-9]+)*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex SepOnly = new($"^{Sep}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex GroupRx = new(@"[0-9]+", RegexOptions.Compiled);
    static readonly Regex RunRx = new(@"[0-9A-Za-z]+", RegexOptions.Compiled);
    static readonly Regex PanRx = new(@"(?<![A-Za-z0-9])[A-Z]{3}[PCHABGJLFT][A-Z] ?[0-9]{4} ?[A-Z](?![A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex PanLooseRx = new(@"(?<![A-Za-z0-9])[A-Za-z0-9]{5} ?[A-Za-z0-9]{4} ?[A-Za-z0-9](?![A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex EmailRx = new(@"(?<![A-Za-z0-9._%+\-])[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Keywords. 'A' Aadhaar, 'V' VID, 'P' phone, 'N' PAN.
    const RegexOptions KwOpts = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    static readonly (char Kind, Regex Rx)[] KeywordRx =
    {
        ('A', new Regex(@"(?<![A-Za-z])(?:aadhaar|aadhar|adhaar|adhar|uidai|uid)(?![A-Za-z])|ఆధార|आधार|ஆதார|ಆಧಾರ|ആധാർ|আধার", KwOpts)),
        ('V', new Regex(@"(?<![A-Za-z])(?:vid|virtual\s*id)(?![A-Za-z])", KwOpts)),
        ('P', new Regex(@"(?<![A-Za-z])(?:mob|mobile|ph|phone|cell|contact|tel|whatsapp)(?![A-Za-z])|ఫోన్|సెల్|మొబైల్|फ़ोन|फोन|मोबाइल|தொலைபேசி|ಫೋನ್|ಮೊಬೈಲ್", KwOpts)),
        ('N', new Regex(@"(?<![A-Za-z])pan(?![A-Za-z])|permanent\s+account|పాన్|पैन|पॅन|பான்|ಪ್ಯಾನ್", KwOpts)),
    };
    static readonly Regex LabelTailRx = new(@"^\s*(?:(?:no|nos|number|num|నంబర్|నెం|సంఖ్య|संख्या|नंबर|नं)\.?)?\s*[:\-–—.]*\s*$", KwOpts);

    // A currency marker directly before a number (as a whole word, so "Teachers 9849012345" is not an amount).
    static readonly Regex CurrencyRx = new(@"(?<![\p{L}\p{M}])(?:rs\.?|inr|usd|rupees|₹|\$|రూ\.?|रु\.?|ரூ\.?|ರೂ\.?|रुपये|रुपए)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Every code unit that is a Unicode decimal digit becomes '0'..'9'; other characters are kept. Same length as the input.</summary>
    public static string NormalizeDigits(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c >= '0' && c <= '9') sb.Append(c);
            else if (c > 0x7F && char.IsDigit(c)) sb.Append((char)('0' + (int)char.GetNumericValue(c)));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    readonly record struct Kw(int End, char Kind);

    static List<Kw> Keywords(string s)
    {
        var l = new List<Kw>();
        foreach (var (kind, rx) in KeywordRx)
            foreach (Match m in rx.Matches(s)) l.Add(new Kw(m.Index + m.Length, kind));
        return l;
    }

    /// <summary>Number of word-like tokens (containing a letter or digit) in s[from..to).</summary>
    static int TokenDistance(string s, int from, int to)
    {
        int count = 0; bool inTok = false, alnum = false;
        for (int i = from; i < to; i++)
        {
            if (char.IsWhiteSpace(s[i])) { if (inTok && alnum) count++; inTok = false; alnum = false; }
            else { inTok = true; if (char.IsLetterOrDigit(s[i])) alnum = true; }
        }
        if (inTok && alnum) count++;
        return count;
    }

    const int KeywordWindowTokens = 3;

    /// <summary>True when one of the given keyword kinds ends before <paramref name="pos"/> with at most three word tokens in between.</summary>
    static bool KeywordNear(string s, List<Kw> kws, int pos, string kinds)
    {
        foreach (var k in kws)
            if (kinds.IndexOf(k.Kind) >= 0 && k.End <= pos && TokenDistance(s, k.End, pos) <= KeywordWindowTokens) return true;
        return false;
    }

    /// <summary>True when the line ends with a keyword (Aadhaar, UID, VID, Mob, Phone ...) and an optional "No:" label, so the number is probably on the next row.</summary>
    public static bool EndsWithLabel(string text)
    {
        var s = NormalizeDigits(text);
        int best = -1;
        foreach (var k in Keywords(s)) if (k.Kind != 'N' && k.End > best) best = k.End;
        return best >= 0 && LabelTailRx.IsMatch(s.Substring(best));
    }

    static bool IsDigitish(string run)
    {
        int digits = 0;
        foreach (char c in run) if (c >= '0' && c <= '9') digits++;
        if (digits == run.Length) return true;
        if (run.Length < 3 || digits < 2 || digits * 2 < run.Length) return false;
        foreach (char c in run) if (!(c >= '0' && c <= '9') && "OolISB".IndexOf(c) < 0) return false;
        return true;
    }

    static char ToDigit(char c) => c switch { 'O' or 'o' => '0', 'l' or 'I' => '1', 'S' => '5', 'B' => '8', _ => c };

    /// <summary>
    /// Near a keyword, inside a digit group that is otherwise digits, O/o become 0, l/I become 1, S becomes 5 and B becomes 8 (the
    /// usual OCR slips). Same length as the input.
    /// </summary>
    static string FixConfusions(string s, List<Kw> kws)
    {
        if (!kws.Any(k => k.Kind != 'N')) return s;
        var runs = RunRx.Matches(s);
        char[]? chars = null;
        int i = 0;
        while (i < runs.Count)
        {
            if (!IsDigitish(runs[i].Value)) { i++; continue; }
            int j = i;
            while (j + 1 < runs.Count && IsDigitish(runs[j + 1].Value)
                   && SepOnly.IsMatch(s.Substring(runs[j].Index + runs[j].Length, runs[j + 1].Index - runs[j].Index - runs[j].Length))) j++;
            for (int k = i; k <= j; k++)
            {
                if (!KeywordNear(s, kws, runs[k].Index, "AVP")) continue;
                for (int r = k; r <= j; r++)
                    for (int p = runs[r].Index; p < runs[r].Index + runs[r].Length; p++)
                    {
                        char d = ToDigit(s[p]);
                        if (d != s[p]) { chars ??= s.ToCharArray(); chars[p] = d; }
                    }
                break;
            }
            i = j + 1;
        }
        return chars == null ? s : new string(chars);
    }

    public static List<PiiMatch> Find(string text, RedactionOptions o)
    {
        var s0 = NormalizeDigits(text);
        var kws = Keywords(s0);
        var s = FixConfusions(s0, kws);
        // Priority: 0 verified Aadhaar, 1 verified VID, 2 phone, 3 unverified Aadhaar/VID, 4 PAN, 5 e-mail.
        var cands = new List<(int Prio, PiiMatch M)>();

        if (o.Aadhaar || o.Vid || o.Phone) Numbers(s, kws, o, cands);
        if (o.Pan)
        {
            foreach (Match m in PanRx.Matches(s0)) cands.Add((4, new PiiMatch(PiiKind.Pan, m.Index, m.Length)));
            foreach (Match m in PanLooseRx.Matches(s0))
            {
                if (!KeywordNear(s0, kws, m.Index, "N")) continue;
                var fixedPan = FixPan(m.Value);
                if (fixedPan == null) continue;
                cands.Add((4, new PiiMatch(PiiKind.Pan, m.Index, m.Length, m.Value.Replace(" ", "") == fixedPan)));
            }
        }
        if (o.Email)
            foreach (Match m in EmailRx.Matches(s0)) cands.Add((5, new PiiMatch(PiiKind.Email, m.Index, m.Length)));

        // Longest first (so a 16-digit VID beats the 12-digit Aadhaar inside it), then priority, then leftmost; keep non-overlapping.
        var accepted = new List<PiiMatch>();
        foreach (var (_, m) in cands.OrderByDescending(c => c.M.Length).ThenBy(c => c.Prio).ThenBy(c => c.M.Start))
            if (!accepted.Any(a => m.Start < a.Start + a.Length && a.Start < m.Start + m.Length)) accepted.Add(m);
        // With VID detection off the 16-digit candidates still took part above, so the 12 digits inside a VID are not reported as an Aadhaar.
        if (!o.Vid) accepted.RemoveAll(m => m.Kind == PiiKind.Vid);
        accepted.Sort((a, b) => a.Start.CompareTo(b.Start));
        return accepted;
    }

    static void Numbers(string s, List<Kw> kws, RedactionOptions o, List<(int Prio, PiiMatch M)> cands)
    {
        var chains = DigitChain.Matches(s).Select(m => (Start: m.Index, End: m.Index + m.Length, Digits: m.Value.Count(c => c >= '0' && c <= '9'))).ToList();
        foreach (Match chain in DigitChain.Matches(s))
        {
            var groups = GroupRx.Matches(chain.Value);
            bool grouped444 = groups.Count == 3 && groups.All(g => g.Length == 4) && !chain.Value.Contains('.');
            for (int i = 0; i < groups.Count; i++)
            {
                var digits = new StringBuilder();
                bool dotSep = false;
                for (int j = i; j < groups.Count; j++)
                {
                    if (j > i && chain.Value.AsSpan(groups[j - 1].Index + groups[j - 1].Length, groups[j].Index - groups[j - 1].Index - groups[j - 1].Length).Contains('.')) dotSep = true;
                    digits.Append(groups[j].Value);
                    if (digits.Length > 16) break;
                    // dots join groups only in phone-like "98490.12345" notation; "12.05.2023" and "123.45" are dates and decimals
                    if (dotSep && Enumerable.Range(i, j - i + 1).Any(g => groups[g].Length < 3)) continue;
                    int start = chain.Index + groups[i].Index;
                    int end = chain.Index + groups[j].Index + groups[j].Length;
                    var d = digits.ToString();
                    bool whole = i == 0 && j == groups.Count - 1;

                    if (d.Length == 12 && o.Aadhaar)
                    {
                        if (Isolated(s, start, end, chains))
                        {
                            if (d[0] >= '2' && PiiVerhoeff.IsValid(d)) cands.Add((0, new PiiMatch(PiiKind.Aadhaar, start, end - start)));
                            else if (KeywordNear(s, kws, start, "A") || (whole && grouped444 && d[0] >= '2'))
                                cands.Add((3, new PiiMatch(PiiKind.Aadhaar, start, end - start, false)));
                        }
                    }
                    else if (d.Length == 16 && (o.Vid || o.Aadhaar))
                    {
                        if (Isolated(s, start, end, chains))
                        {
                            if (d[0] >= '2' && PiiVerhoeff.IsValid(d)) cands.Add((1, new PiiMatch(PiiKind.Vid, start, end - start)));
                            else if (KeywordNear(s, kws, start, "VA")) cands.Add((3, new PiiMatch(PiiKind.Vid, start, end - start, false)));
                        }
                    }
                    if (o.Phone && IsPhoneDigits(d) && Isolated(s, start, end, chains))
                    {
                        int ps = start;
                        // keep a "+" (as in +91) inside the redaction
                        int k = ps - 1;
                        while (k >= 0 && (s[k] == ' ' || s[k] == '(')) k--;
                        if (k >= 0 && s[k] == '+') ps = k;
                        cands.Add((2, new PiiMatch(PiiKind.Phone, ps, end - ps)));
                    }
                }
            }
        }
    }

    /// <summary>Repairs a loosely matched PAN (OCR slips near a PAN keyword), or null when it cannot be a PAN.</summary>
    static string? FixPan(string raw)
    {
        var c = raw.Replace(" ", "");
        if (c.Length != 10) return null;
        var sb = new StringBuilder(10);
        for (int i = 0; i < 10; i++)
        {
            char ch = c[i];
            if (i < 5 || i == 9)
            {
                ch = ch switch { '0' => 'O', '1' => 'I', '5' => 'S', '8' => 'B', _ => char.ToUpperInvariant(ch) };
                if (ch < 'A' || ch > 'Z') return null;
            }
            else
            {
                ch = ToDigit(ch);
                if (ch == 'i') ch = '1';
                if (ch < '0' || ch > '9') return null;
            }
            sb.Append(ch);
        }
        return "PCHABGJLFT".IndexOf(sb[3]) >= 0 ? sb.ToString() : null;
    }

    /// <summary>10 digits starting 6-9, optionally with a 0, 91, 091 or 0091 prefix.</summary>
    static bool IsPhoneDigits(string d)
    {
        bool Mobile(int at) => d.Length - at == 10 && d[at] >= '6';
        return d.Length switch
        {
            10 => Mobile(0),
            11 => d[0] == '0' && Mobile(1),
            12 => d.StartsWith("91", StringComparison.Ordinal) && Mobile(2),
            13 => d.StartsWith("091", StringComparison.Ordinal) && Mobile(3),
            14 => d.StartsWith("0091", StringComparison.Ordinal) && Mobile(4),
            _ => false,
        };
    }

    /// <summary>
    /// Rejects numbers that are glued to other tokens: an ASCII letter or "_" next to them (invoice numbers), a "/" next to a short
    /// token (survey numbers; "9849012345/9849012346" is two phone numbers and stays), a "." or "," joining them to another digit
    /// (amounts, decimals), or a currency marker in front (Rs. 9876543210).
    /// </summary>
    static bool Isolated(string s, int start, int end, List<(int Start, int End, int Digits)> chains)
    {
        if (start > 0)
        {
            char p = s[start - 1];
            if (IsAsciiLetter(p) || p == '_') return false;
            if (p == '/' && !SlashNeighbourIsNumber(s, start - 1, chains, before: true)) return false;
            if ((p == '.' || p == ',') && start >= 2 && s[start - 2] >= '0' && s[start - 2] <= '9') return false;
            if (CurrencyRx.IsMatch(s.Substring(0, start))) return false;
        }
        if (end < s.Length)
        {
            char n = s[end];
            if (IsAsciiLetter(n) || n == '_') return false;
            if (n == '/' && !SlashNeighbourIsNumber(s, end, chains, before: false)) return false;
            if ((n == '.' || n == ',') && end + 1 < s.Length && s[end + 1] >= '0' && s[end + 1] <= '9') return false;
        }
        return true;
    }

    /// <summary>For a "/" at <paramref name="slash"/>: true when the thing on the other side is itself a full number (10+ digits) or nothing; false when it is a short alphanumeric token.</summary>
    static bool SlashNeighbourIsNumber(string s, int slash, List<(int Start, int End, int Digits)> chains, bool before)
    {
        if (before)
        {
            int j = slash - 1;
            while (j >= 0 && s[j] == ' ') j--;
            if (j < 0 || !char.IsLetterOrDigit(s[j])) return true;
            foreach (var c in chains) if (c.End == j + 1 && c.Digits >= 10) return true;
            return false;
        }
        else
        {
            int j = slash + 1;
            while (j < s.Length && s[j] == ' ') j++;
            if (j >= s.Length || !char.IsLetterOrDigit(s[j])) return true;
            foreach (var c in chains) if (c.Start == j && c.Digits >= 10) return true;
            return false;
        }
    }

    static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
