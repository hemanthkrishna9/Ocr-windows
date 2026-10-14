using System.Drawing;
using System.IO.Compression;
using System.Text;
using IndicOcr.Core.Internal;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Xunit;
using Xunit.Abstractions;

namespace IndicOcr.Core.Tests;

public class RedactionTests
{
    readonly ITestOutputHelper _out;
    public RedactionTests(ITestOutputHelper o) => _out = o;

    // ---- helpers -------------------------------------------------------------------------------------------------------------

    /// <summary>A synthetic, checksum-valid Aadhaar-format number (never a real person's): 11 payload digits + Verhoeff check digit.</summary>
    static string Aadhaar(string payload11) => payload11 + PiiVerhoeff.CheckDigit(payload11);
    static string Group(string d) => $"{d[..4]} {d[4..8]} {d[8..]}";

    const string Pan = "ABCPE1234F";
    const string Phone = "9876543210";

    static OcrPage PageOf(params string[] lines)
    {
        var list = new List<OcrLine>();
        for (int i = 0; i < lines.Length; i++)
        {
            var box = new RectangleF(100, 100 + i * 80, 1000, 50);
            list.Add(new OcrLine(lines[i], 0.9f, OcrLanguage.English, box,
                new[] { new PointF(box.Left, box.Top), new PointF(box.Right, box.Top), new PointF(box.Right, box.Bottom), new PointF(box.Left, box.Bottom) }));
        }
        return new OcrPage(0, 1400, 100 + lines.Length * 80 + 100, list, TimeSpan.Zero);
    }

    static IReadOnlyList<PiiFinding> Find(string text, RedactionOptions? o = null) => Redactor.Find(PageOf(text), o);
    static string Mask(string asciiDigits, string indicZero) => new(asciiDigits.Select(c => (char)(indicZero[0] + (c - '0'))).ToArray());

    // ---- Verhoeff --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Verhoeff_matches_the_published_example_and_rejects_every_single_digit_error()
    {
        Assert.Equal('3', PiiVerhoeff.CheckDigit("236"));
        Assert.True(PiiVerhoeff.IsValid("2363"));
        Assert.False(PiiVerhoeff.IsValid("2364"));
        var a = Aadhaar("23456789012");
        Assert.True(PiiVerhoeff.IsValid(a));
        for (int i = 0; i < a.Length; i++)
            for (char d = '0'; d <= '9'; d++)
                if (d != a[i]) Assert.False(PiiVerhoeff.IsValid(a[..i] + d + a[(i + 1)..]));
        // every adjacent transposition of different digits is also caught
        for (int i = 0; i + 1 < a.Length; i++)
            if (a[i] != a[i + 1]) Assert.False(PiiVerhoeff.IsValid(a[..i] + a[i + 1] + a[i] + a[(i + 2)..]));
    }

    // ---- Aadhaar ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("23456789012")]
    [InlineData("98765432101")]
    [InlineData("50000000001")]
    public void Aadhaar_is_found_spaced_hyphenated_and_contiguous(string payload)
    {
        var a = Aadhaar(payload);
        foreach (var text in new[] { $"Aadhaar No: {Group(a)}", $"UID {a[..4]}-{a[4..8]}-{a[8..]}", $"No.{a}", $"{Group(a)}" })
        {
            var f = Assert.Single(Find(text));
            Assert.Equal(PiiKind.Aadhaar, f.Kind);
            Assert.Equal(text.IndexOf(a[..4], StringComparison.Ordinal), f.Start);
            Assert.EndsWith(a[8..], text[f.Start..(f.Start + f.Length)].Replace(" ", "").Replace("-", ""));
        }
    }

    [Fact]
    public void Aadhaar_masked_text_shows_only_the_last_four_digits()
    {
        var a = Aadhaar("23456789012");
        var f = Assert.Single(Find($"Aadhaar {Group(a)}"));
        Assert.Equal("XXXX XXXX " + a[8..], f.MaskedText);
        Assert.DoesNotContain(a[..8], f.MaskedText);
    }

    [Fact]
    public void Aadhaar_with_ocr_spacing_inside_groups_is_found()
    {
        var a = Aadhaar("34567890123");
        var f = Assert.Single(Find($"UID: {a[..4]} {a[4..8]} {a[8..11]} {a[11..]}"));
        Assert.Equal(PiiKind.Aadhaar, f.Kind);
        Assert.Equal(4, f.Start - "UID: ".Length + 4);
    }

    [Fact]
    public void Aadhaar_is_found_after_an_unrelated_number_in_the_same_digit_run()
    {
        var a = Aadhaar("23456789012");
        var f = Assert.Single(Find($"Total 5000 {Group(a)}"));
        Assert.Equal(PiiKind.Aadhaar, f.Kind);
        Assert.Equal("Total 5000 ".Length, f.Start);
    }

    [Fact]
    public void Twelve_digits_that_fail_the_checksum_are_flagged_only_with_grouping_or_a_keyword()
    {
        var a = Aadhaar("23456789012");
        var bad = a[..11] + (char)('0' + (a[11] - '0' + 1) % 10);
        Assert.Empty(Find($"Ref {bad}"));                          // ungrouped, no keyword: needs the checksum
        Assert.Empty(Find($"Ref {Aadhaar("13456789012")}"));       // starts with 1
        var g = Assert.Single(Find($"Ref {Group(bad)}"));          // 4-4-4 grouping: flagged, unverified
        Assert.Equal(PiiKind.Aadhaar, g.Kind);
        Assert.False(g.Verified);
        Assert.Empty(Find($"Ref {Group(Aadhaar("13456789012"))}")); // grouped but cannot start with 0 or 1
        Assert.Empty(Find($"Ref {Group(Aadhaar("03456789012"))}"));
        Assert.Empty(Find($"Cards {Group(bad)} 1111"));           // part of a longer grouped number
        var k = Assert.Single(Find($"Aadhaar No: {bad}"));         // keyword: flagged even ungrouped
        Assert.False(k.Verified);
        Assert.True(Assert.Single(Find($"Aadhaar No: {Group(a)}")).Verified);
    }

    [Fact]
    public void Vid_is_found_by_default_and_can_be_switched_off_without_a_partial_aadhaar_hit()
    {
        var v = Aadhaar("234567890123456".Substring(0, 15));
        Assert.Equal(16, v.Length);
        var text = $"VID: {v[..4]} {v[4..8]} {v[8..12]} {v[12..]}";
        var f = Assert.Single(Find(text));
        Assert.Equal(PiiKind.Vid, f.Kind);
        Assert.Equal(16 + 3, f.Length);                              // all 16 digits, not just the first 12
        Assert.True(f.Verified);
        Assert.Empty(Find(text, new RedactionOptions { Vid = false }));
    }

    [Fact]
    public void Vid_with_a_misread_digit_is_still_found_after_a_VID_keyword()
    {
        var v = Aadhaar("234567890123456".Substring(0, 15));
        var bad = v[..15] + (char)('0' + (v[15] - '0' + 1) % 10);
        var f = Assert.Single(Find($"VID: {bad[..4]} {bad[4..8]} {bad[8..12]} {bad[12..]}"));
        Assert.Equal(PiiKind.Vid, f.Kind);
        Assert.False(f.Verified);
        Assert.Empty(Find($"Card {bad[..4]} {bad[4..8]} {bad[8..12]} {bad[12..]}")); // card-like, no keyword
    }

    // ---- PAN -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("PAN: ABCPE1234F")]
    [InlineData("Permanent Account Number ABCPE1234F.")]
    [InlineData("ABCHE1234F")]
    [InlineData("PAN ABCPE 1234 F")]
    public void Pan_is_found(string text)
    {
        var f = Assert.Single(Find(text));
        Assert.Equal(PiiKind.Pan, f.Kind);
    }

    [Theory]
    [InlineData("ABCXE1234F")]      // 4th letter is not a holder type
    [InlineData("ABCPE12345")]      // last char not a letter
    [InlineData("XABCPE1234F")]     // glued to a longer token
    [InlineData("ABCPE1234FG")]
    [InlineData("abcpe1234f")]
    [InlineData("INV2024ABC")]
    public void Pan_lookalikes_are_not_found(string text) => Assert.Empty(Find(text));

    [Fact]
    public void Pan_masked_text_hides_all_but_the_last_four()
    {
        Assert.Equal("XXXXXX234F", Assert.Single(Find("PAN " + Pan)).MaskedText);
    }

    // ---- Phone -----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Mobile: 9876543210")]
    [InlineData("Ph 98765 43210")]
    [InlineData("Ph 98765-43210")]
    [InlineData("Cell +91 98765 43210")]
    [InlineData("Cell +91-9876543210")]
    [InlineData("Cell 91 9876543210")]
    [InlineData("Cell 09876543210")]
    [InlineData("Cell 0 9876543210")]
    [InlineData("Cell (+91) 9876543210")]
    [InlineData("Cell:9876543210,")]
    [InlineData("Cell 987 654 3210")]
    [InlineData("Cell 6000000000")]
    public void Phone_is_found(string text)
    {
        var f = Assert.Single(Find(text));
        Assert.Equal(PiiKind.Phone, f.Kind);
        if (!text.Contains("6000000000")) Assert.EndsWith("3210", f.MaskedText);
    }

    [Fact]
    public void Phone_plus_prefix_is_inside_the_finding()
    {
        var text = "Cell +91 98765 43210 ok";
        var f = Assert.Single(Find(text));
        Assert.Equal(text.IndexOf('+'), f.Start);
        Assert.Equal("+91 98765 43210".Length, f.Length);
        Assert.Equal("+XX XXXXXX3210".Insert(0, "").Replace("XXXXXX3210", "XXXXX X3210"), f.MaskedText);
    }

    [Theory]
    [InlineData("Phone 5876543210")]       // starts with 5
    [InlineData("Phone 987654321")]        // 9 digits
    [InlineData("Phone 98765432109876")]   // too long
    [InlineData("Pincode 500081")]
    [InlineData("Pincode: 500 081")]
    public void Phone_lookalikes_are_not_found(string text) => Assert.Empty(Find(text));

    // ---- Email -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Email_is_found_only_when_enabled_and_masked_for_display()
    {
        var text = "Mail rama.rao@example.co.in now";
        Assert.Empty(Find(text));
        var f = Assert.Single(Find(text, new RedactionOptions { Email = true }));
        Assert.Equal(PiiKind.Email, f.Kind);
        Assert.Equal("r***@example.co.in", f.MaskedText);
        Assert.Equal("rama.rao@example.co.in", text.Substring(f.Start, f.Length));
    }

    // ---- False positives -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Date: 15-08-2024")]
    [InlineData("Date 15/08/2024 10:30")]
    [InlineData("దినాంకం 15-08-2024")]
    [InlineData("Rs. 12,34,567")]
    [InlineData("Rs. 9876543210")]
    [InlineData("Total ₹ 9876543210")]
    [InlineData("INR 234567890123")]
    [InlineData("Amount 1,23,45,678.90")]
    [InlineData("Sy.No.123/4A")]
    [InlineData("సర్వే నెం. 123/4A, 5/2")]
    [InlineData("Invoice No: INV/2024/9876543210")]
    [InlineData("Invoice INV9876543210")]
    [InlineData("Pincode 500081, Hyderabad")]
    [InlineData("Order 12345678")]
    [InlineData("Case No. O.S. 234/2019")]
    [InlineData("Acct 1234567890123456789")]
    public void Dates_amounts_survey_numbers_pincodes_and_invoice_numbers_are_not_flagged(string text)
    {
        var f = Find(text, new RedactionOptions { Vid = true, Email = true });
        Assert.Empty(f);
    }

    // ---- Indic digits ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("౦")] // Telugu
    [InlineData("०")] // Devanagari
    [InlineData("௦")] // Tamil
    [InlineData("೦")] // Kannada
    public void Indic_digits_are_matched_for_every_kind(string zero)
    {
        var a = Aadhaar("23456789012");
        var f = Find($"Aadhaar {Mask(Group(a).Replace(" ", ""), zero).Insert(8, " ").Insert(4, " ")}  Ph {Mask(Phone, zero)}  PAN {Pan[..5]}{Mask("1234", zero)}{Pan[9]}");
        Assert.Equal(new[] { PiiKind.Aadhaar, PiiKind.Phone, PiiKind.Pan }, f.Select(x => x.Kind).ToArray());
        Assert.Equal("XXXX XXXX " + a[8..], f[0].MaskedText); // display always in ASCII
    }

    [Fact]
    public void Normalisation_keeps_the_length_so_spans_map_back()
    {
        var s = "ఫోన్ ౯౮౭౬౫ ౪౩౨౧౦ abc ०१२३";
        var n = PiiDetector.NormalizeDigits(s);
        Assert.Equal(s.Length, n.Length);
        Assert.Contains("98765 43210", n);
        Assert.EndsWith("0123", n);
    }

    // ---- Priorities / combos ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Several_findings_on_one_line_are_all_reported_in_order()
    {
        var a = Aadhaar("23456789012");
        var f = Find($"{Group(a)} / {Pan} / +91 {Phone}");
        Assert.Equal(new[] { PiiKind.Aadhaar, PiiKind.Pan, PiiKind.Phone }, f.Select(x => x.Kind).ToArray());
        Assert.True(f[0].Start < f[1].Start && f[1].Start < f[2].Start);
    }

    [Fact]
    public void Findings_and_their_ToString_never_contain_the_matched_value()
    {
        var a = Aadhaar("23456789012");
        foreach (var f in Redactor.Find(PageOf($"{Group(a)}", Pan, Phone)))
        {
            var s = f.ToString();
            Assert.DoesNotContain(a, s.Replace(" ", ""));
            Assert.DoesNotContain(Pan, s);
            Assert.DoesNotContain(Phone, s);
        }
    }

    // ---- Evidence beyond the checksum -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ఆధార్ సంఖ్య: ")]
    [InlineData("आधार संख्या ")]
    [InlineData("ஆதார் எண் ")]
    [InlineData("ಆಧಾರ್ ಸಂಖ್ಯೆ: ")]
    [InlineData("UID: ")]
    [InlineData("UIDAI ")]
    [InlineData("Aadhar No. ")]
    [InlineData("AADHAAR ")]
    public void A_misread_aadhaar_after_an_aadhaar_keyword_is_flagged_as_unverified(string keyword)
    {
        var a = Aadhaar("23456789012");
        var bad = a[..11] + (char)('0' + (a[11] - '0' + 3) % 10);
        Assert.False(PiiVerhoeff.IsValid(bad));
        foreach (var number in new[] { bad, Group(bad) })
        {
            var f = Assert.Single(Find(keyword + number));
            Assert.Equal(PiiKind.Aadhaar, f.Kind);
            Assert.False(f.Verified);
            Assert.Equal(keyword.Length, f.Start);
        }
    }

    [Fact]
    public void The_keyword_must_be_close_to_the_number()
    {
        var a = Aadhaar("23456789012");
        var bad = a[..11] + (char)('0' + (a[11] - '0' + 3) % 10);
        Assert.Single(Find($"Aadhaar No is {bad}"));                                  // three words in between: still near
        Assert.Empty(Find($"Aadhaar was never printed on this old form {bad}"));
        Assert.Empty(Find($"Reference {bad}"));
    }

    [Fact]
    public void Ocr_letter_lookalikes_inside_a_digit_group_near_a_keyword_count_as_digits()
    {
        // OCR slips are single characters: write ONE digit of a valid number as a lookalike letter.
        static string Slip(string n, char digit, char letter) { int i = n.IndexOf(digit); return i < 0 ? n : n[..i] + letter + n[(i + 1)..]; }
        var a = Aadhaar("20158000051");
        Assert.True(a.Contains('0') && a.Contains('1') && a.Contains('5') && a.Contains('8'));
        foreach (var variant in new[] { Slip(a, '0', 'O'), Slip(a, '0', 'o'), Slip(a, '1', 'l'), Slip(a, '1', 'I'), Slip(a, '5', 'S'), Slip(a, '8', 'B') })
        {
            Assert.NotEqual(a, variant);
            var text = $"Aadhaar No: {Group(variant)}";
            var f = Assert.Single(Find(text));
            Assert.Equal(PiiKind.Aadhaar, f.Kind);
            Assert.Equal("Aadhaar No: ".Length, f.Start);          // the span covers the ORIGINAL characters
            Assert.Equal(14, f.Length);
            Assert.True(f.Verified, "the repaired digits pass the checksum");
            Assert.Empty(Find(Group(variant)));                    // no keyword, no repair
            Assert.Empty(Find($"Total {Group(variant)}"));
        }
        var g = Assert.Single(Find("Mob: 9849O 12345"));
        Assert.Equal(PiiKind.Phone, g.Kind);
        Assert.Equal("Mob: ".Length, g.Start);
        Assert.Empty(Find("9849O 12345"));
        Assert.Empty(Find("Mob: BOSS and OIL"));                  // words are not numbers
    }

    [Fact]
    public void A_vid_keeps_all_sixteen_digits_even_when_the_aadhaar_inside_it_is_valid()
    {
        var a = Aadhaar("23456789012");
        var v16 = a + "0005";
        var f = Assert.Single(Find($"VID: {Group(a)} 0005"));
        Assert.Equal(PiiKind.Vid, f.Kind);
        Assert.Equal("VID: ".Length, f.Start);
        Assert.Equal(19, f.Length);
        Assert.Single(Find($"VID: {v16}"));
        Assert.Equal(16, Find($"VID: {v16}")[0].Length);
    }

    // ---- Slash, currency, separators -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Mob:9849012345/9849012346", 2)]
    [InlineData("Mob: 9849012345 / 9849012346", 2)]
    [InlineData("Ph 98490 12345/98490 12346", 2)]
    [InlineData("Cell 9849012345/6", 0)]       // survey style: short token on the other side
    [InlineData("Sy.No 123/9849012345", 0)]
    [InlineData("INV/2024/9876543210", 0)]
    [InlineData("9876543210/4A", 0)]
    public void A_slash_rejects_only_when_the_other_side_is_a_short_token(string text, int expected)
    {
        Assert.Equal(expected, Find(text).Count(f => f.Kind == PiiKind.Phone));
    }

    [Theory]
    [InlineData("Teachers 9849012345")]
    [InlineData("Hours 9849012345")]
    [InlineData("Mob 9849012345 Rs")]
    public void Currency_markers_are_whole_words(string text) => Assert.Single(Find(text));

    [Theory]
    [InlineData("Rs 9849012345")]
    [InlineData("Rs. 9849012345")]
    [InlineData("RS.9849012345")]
    [InlineData("INR 9849012345")]
    [InlineData("Rupees 9849012345")]
    [InlineData("₹9849012345")]
    [InlineData("రూ. 9849012345")]
    [InlineData("రూ.9849012345")]
    [InlineData("रु. 9849012345")]
    [InlineData("ரூ 9849012345")]
    [InlineData("ರೂ. 9849012345")]
    public void Amounts_after_a_rupee_marker_are_not_phone_numbers(string text) => Assert.Empty(Find(text));

    [Theory]
    [InlineData("Mob: +91–98490–12345")]      // en dash
    [InlineData("Mob: +91—98490—12345")]      // em dash
    [InlineData("Mob: 98490.12345")]
    [InlineData("Mob: 98490 – 12345")]
    [InlineData("Mob: 98490-12345")]
    public void Dashes_and_dots_may_separate_phone_groups(string text)
    {
        var f = Assert.Single(Find(text));
        Assert.Equal(PiiKind.Phone, f.Kind);
        Assert.Equal("Mob: ".Length, f.Start);
        Assert.Equal(text.Length - f.Start, f.Length);
    }

    [Fact]
    public void Dots_and_dashes_between_aadhaar_groups_are_accepted()
    {
        var a = Aadhaar("23456789012");
        Assert.Single(Find($"Aadhaar {a[..4]}–{a[4..8]}–{a[8..]}"));
        Assert.Single(Find($"Aadhaar {a[..4]}.{a[4..8]}.{a[8..]}"));
    }

    [Theory]
    [InlineData("Date 12.05.2023")]
    [InlineData("Date 12.05.2023 10.30")]
    [InlineData("Amount 9876543210.50")]
    [InlineData("Total 1,234.56")]
    [InlineData("Version 2.3.4.5")]
    [InlineData("Amount 987654321000.00")]
    [InlineData("Date 2023–05–12")]
    public void Dates_and_decimals_stay_unflagged_with_the_new_separators(string text)
        => Assert.Empty(Find(text, new RedactionOptions { Email = true }));

    // ---- PAN with OCR slips ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("PAN: abcpe1234f")]
    [InlineData("PAN: ABCPE1234f")]
    [InlineData("PAN: ABCPEl234F")]
    [InlineData("PAN: ABCPEI234F")]
    [InlineData("PAN: ABCPEO234F")]
    [InlineData("PAN: ABCPE12B4F")]
    [InlineData("PAN: 0BCPE1234F")]
    [InlineData("PAN: A1CPE1234F")]
    [InlineData("పాన్ నెం. ABCPEl234F")]
    [InlineData("पैन ABCPE1234f")]
    [InlineData("Permanent Account Number: abcpe1234f")]
    public void Pan_near_a_keyword_tolerates_case_and_digit_letter_slips(string text)
    {
        var f = Assert.Single(Find(text));
        Assert.Equal(PiiKind.Pan, f.Kind);
        Assert.False(f.Verified);
        Assert.Equal(10, text.Substring(f.Start, f.Length).Replace(" ", "").Length);
    }

    [Theory]
    [InlineData("abcpe1234f")]
    [InlineData("ABCPEl234F")]
    [InlineData("Receipt ABCPEl234F")]
    [InlineData("PAN was lost long ago ABCPEl234F")]
    [InlineData("PAN: ABCXE1234F")]     // 4th letter is not a holder type, whatever the slips
    public void Pan_without_a_keyword_stays_strict(string text) => Assert.Empty(Find(text));

    [Fact]
    public void A_strict_pan_is_verified()
    {
        Assert.True(Assert.Single(Find("PAN: ABCPE1234F")).Verified);
        Assert.True(Assert.Single(Find("ABCPE1234F")).Verified);
        Assert.True(Assert.Single(Find("Ph 9876543210")).Verified);
    }

    // ---- Virtual lines ---------------------------------------------------------------------------------------------------------

    static OcrLine L(string text, float x, float y, float w, float h) => new(text, 0.9f, OcrLanguage.English, new RectangleF(x, y, w, h),
        new[] { new PointF(x, y), new PointF(x + w, y), new PointF(x + w, y + h), new PointF(x, y + h) });

    static OcrPage Custom(params OcrLine[] lines) => new(0, 2200, 1200, lines, TimeSpan.Zero);

    [Fact]
    public void A_big_number_split_into_one_ocr_line_per_group_is_found_and_covered_per_line()
    {
        var a = Aadhaar("23456789012");
        var page = Custom(L("Aadhaar", 100, 100, 330, 90), L(a[..4], 480, 104, 250, 86), L(a[4..8], 900, 100, 250, 90), L(a[8..], 1320, 98, 250, 90));
        var fs = Redactor.Find(page);
        Assert.Equal(3, fs.Count);
        Assert.All(fs, f => { Assert.Equal(PiiKind.Aadhaar, f.Kind); Assert.True(f.Verified); });
        Assert.Single(fs.Select(f => f.GroupId).Distinct());
        Assert.Equal(new[] { 1, 2, 3 }, fs.Select(f => f.LineIndex).ToArray());
        for (int i = 0; i < 3; i++)
        {
            var line = page.Lines[i + 1].Box;
            Assert.True(fs[i].Box.Left <= line.Left && fs[i].Box.Right >= line.Right - 1, "each quad covers its own group");
            Assert.True(fs[i].Box.Right < line.Right + line.Width * 0.5f, "and stays near it");
        }
        Assert.All(fs, f => Assert.DoesNotContain(a[..8], f.MaskedText));
        Assert.Equal("XXXX XXXX " + a[8..], fs[0].MaskedText);
        var masked = Redactor.MaskText(page, fs);
        Assert.Equal(new[] { "Aadhaar", "XXXX", "XXXX", "XXXX" }, masked.Lines.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void A_junk_mark_line_between_the_groups_and_overlapping_boxes_do_not_break_the_number()
    {
        // what the OCR gave on a rotated e-Aadhaar: "2341 2341", a lone minus sign whose box overlaps the next box, "2346"
        var page = Custom(L("2341 2341", 157, 181, 448, 73), L("\u2212", 620, 207, 37, 34), L("2346", 635, 194, 197, 65), L("Aadhaar", 10, 190, 120, 50));
        var a = "234123412346";
        Assert.True(PiiVerhoeff.IsValid(a));
        var fs = Redactor.Find(page);
        Assert.Equal(3, fs.Count);
        Assert.All(fs, f => Assert.Equal(PiiKind.Aadhaar, f.Kind));
    }

    [Fact]
    public void Lines_join_by_baseline_and_distance_only()
    {
        var a = Aadhaar("23456789012");
        // too far apart (more than three line heights)
        Assert.Empty(Redactor.Find(Custom(L(a[..4], 100, 100, 250, 90), L(a[4..8], 900, 100, 250, 90), L(a[8..], 1700, 100, 250, 90))));
        // a different baseline (second line sits well below)
        Assert.Empty(Redactor.Find(Custom(L(a[..4], 100, 100, 250, 90), L(a[4..8], 400, 200, 250, 90), L(a[8..], 700, 300, 250, 90))));
        // slightly staggered boxes of the same baseline still join, in any list order
        var fs = Redactor.Find(Custom(L(a[8..], 760, 112, 250, 80), L(a[..4], 100, 100, 250, 90), L(a[4..8], 430, 106, 250, 84)));
        Assert.Equal(3, fs.Count);
        Assert.Equal(new[] { 0, 1, 2 }, fs.Select(f => f.LineIndex).OrderBy(i => i).ToArray());
    }

    [Fact]
    public void A_split_number_with_a_misread_digit_is_found_through_the_keyword_in_the_row()
    {
        var a = Aadhaar("23456789012");
        var bad = a[..11] + (char)('0' + (a[11] - '0' + 3) % 10);
        var page = Custom(L("ఆధార్ నం.", 100, 100, 300, 80), L(bad[..4], 450, 100, 220, 80), L(bad[4..8], 720, 100, 220, 80), L(bad[8..], 990, 100, 220, 80));
        var fs = Redactor.Find(page);
        Assert.Equal(3, fs.Count);
        Assert.All(fs, f => Assert.False(f.Verified));
    }

    [Fact]
    public void A_number_on_the_row_below_its_keyword_is_found()
    {
        var a = Aadhaar("23456789012");
        var bad = a[..11] + (char)('0' + (a[11] - '0' + 3) % 10);
        // keyword ends the first row; the number is the whole next row, or split in groups
        foreach (var kw in new[] { "Aadhaar", "Aadhaar No:", "ఆధార్ సంఖ్య", "आधार" })
        {
            var fs = Redactor.Find(Custom(L(kw, 100, 100, 420, 70), L(Group(bad), 100, 200, 600, 70)));
            var f = Assert.Single(fs);
            Assert.Equal(1, f.LineIndex);
            Assert.False(f.Verified);
            var split = Redactor.Find(Custom(L(kw, 100, 100, 420, 70), L(bad[..4], 100, 200, 200, 70), L(bad[4..8], 360, 200, 200, 70), L(bad[8..], 620, 200, 200, 70)));
            Assert.Equal(3, split.Count);
        }
        // not under a keyword: no flag; a valid number is found once, not twice
        Assert.Empty(Redactor.Find(Custom(L("Reference", 100, 100, 420, 70), L(bad, 100, 200, 600, 70))));
        Assert.Single(Redactor.Find(Custom(L("Aadhaar", 100, 100, 420, 70), L(Group(a), 100, 200, 600, 70))));
        // phone keyword
        var ph = Redactor.Find(Custom(L("Mobile", 100, 100, 420, 70), L("98490", 100, 200, 250, 70), L("12345", 420, 200, 250, 70)));
        Assert.Equal(2, ph.Count);
        Assert.All(ph, f => Assert.Equal(PiiKind.Phone, f.Kind));
    }

    [Fact]
    public void Joining_lines_does_not_change_findings_inside_a_single_line()
    {
        var a = Aadhaar("23456789012");
        var page = Custom(L($"Aadhaar {Group(a)} Mob {Phone}", 100, 100, 1500, 70), L("PAN " + Pan, 100, 300, 600, 70));
        var fs = Redactor.Find(page);
        Assert.Equal(new[] { PiiKind.Aadhaar, PiiKind.Phone, PiiKind.Pan }, fs.Select(f => f.Kind).ToArray());
        Assert.Equal(new[] { 0, 0, 1 }, fs.Select(f => f.LineIndex).ToArray());
        Assert.Equal(3, fs.Select(f => f.GroupId).Distinct().Count());
    }

    [Fact]
    public void Findings_hold_no_matched_value_even_for_split_and_unverified_matches()
    {
        var a = Aadhaar("23456789012");
        var bad = a[..11] + (char)('0' + (a[11] - '0' + 3) % 10);
        var page = Custom(L("UID", 100, 100, 200, 80), L(bad[..4], 350, 100, 200, 80), L(bad[4..8], 600, 100, 200, 80), L(bad[8..], 850, 100, 200, 80), L("PAN: abcpe1234f", 100, 300, 600, 80));
        foreach (var f in Redactor.Find(page))
        {
            var t = f.ToString().Replace(" ", "");
            Assert.DoesNotContain(bad[..8], t);
            Assert.DoesNotContain("abcpe", t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(f.MaskedText.Replace(" ", ""), bad[..8]);
        }
    }

    // ---- Geometry --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Box_follows_the_position_in_the_line_and_is_widened()
    {
        var a = Aadhaar("23456789012");
        var text = "Aadhaar No: " + Group(a);
        var page = PageOf(text);
        var f = Assert.Single(Redactor.Find(page));
        var line = page.Lines[0].Box;
        // The number is the right half of the text: the region starts right of the middle minus padding and reaches the line's right edge.
        Assert.True(f.Box.Left > line.Left + line.Width * 0.3f, $"left {f.Box.Left}");
        Assert.True(f.Box.Right >= line.Right - 1);
        // padded above and below the line
        Assert.True(f.Box.Top < line.Top && f.Box.Bottom > line.Bottom);
        // true share of the number is about 14/26 of the line; check the box is not much smaller than that (widening, not shrinking)
        Assert.True(f.Box.Width > line.Width * 0.4f);
    }

    [Fact]
    public void Box_is_clamped_to_the_page_and_quad_has_four_corners()
    {
        var page = PageOf(Phone);
        var f = Assert.Single(Redactor.Find(page));
        Assert.Equal(4, f.Quad.Length);
        Assert.True(f.Box.Left >= 0 && f.Box.Top >= 0 && f.Box.Right <= page.Width && f.Box.Bottom <= page.Height);
    }

    [Fact]
    public void Skewed_line_gives_a_skewed_quad()
    {
        // 3 degree slant: right end sits 52 px lower than the left end.
        var poly = new[] { new PointF(100, 100), new PointF(1100, 152), new PointF(1100, 202), new PointF(100, 150) };
        var line = new OcrLine("PAN " + Pan, 0.9f, OcrLanguage.English, new RectangleF(100, 100, 1000, 102), poly);
        var page = new OcrPage(0, 1400, 400, new[] { line }, TimeSpan.Zero);
        var f = Assert.Single(Redactor.Find(page));
        Assert.True(f.Quad[1].Y - f.Quad[0].Y > 20 || f.Quad[0].Y > 120, "quad top edge should follow the slant");
        Assert.True(f.Quad[1].Y > f.Quad[0].Y);
        Assert.True(f.Quad[1].X - f.Quad[0].X > 300);
    }

    [Fact]
    public void Combining_marks_have_no_width_in_the_estimate()
    {
        var w = PiiGeometry.Weights("\u0C15\u0301" + "1\u0C4D");
        Assert.Equal(0, w[1], 3); // combining mark
        Assert.Equal(0, w[3], 3); // virama
    }

    // ---- Apply -----------------------------------------------------------------------------------------------------------------

    static SKBitmap GrayPage(int w, int h)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var c = new SKCanvas(bmp);
        c.Clear(new SKColor(200, 200, 200));
        return bmp;
    }

    [Fact]
    public void Apply_paints_black_over_the_region_masks_the_text_and_does_not_touch_the_inputs()
    {
        var a = Aadhaar("23456789012");
        var page = PageOf($"Aadhaar {Group(a)}", "nothing here", $"PAN {Pan}");
        var origText = page.Text;
        using var img = GrayPage(page.Width, page.Height);
        var before = img.Bytes.ToArray();
        var findings = Redactor.Find(page);
        Assert.Equal(2, findings.Count);

        var (red, rp) = Redactor.Apply(img, page, findings);
        using (red)
        {
            Assert.Equal(before, img.Bytes.ToArray());            // input bitmap untouched
            Assert.Equal(origText, page.Text);                    // input page untouched
            Assert.NotSame(page, rp);
            Assert.Same(page.Lines[1], rp.Lines[1]);              // unselected line is shared, not copied

            foreach (var f in findings)
            {
                // the middle of the region is pure black and fully opaque
                var cx = (int)(f.Box.Left + f.Box.Width / 2); var cy = (int)(f.Box.Top + f.Box.Height / 2);
                Assert.Equal(new SKColor(0, 0, 0, 255), red.GetPixel(cx, cy));
            }
            Assert.Equal(new SKColor(200, 200, 200, 255), red.GetPixel(5, 5));
            Assert.DoesNotContain(a, rp.Text.Replace(" ", ""));
            Assert.DoesNotContain(Pan, rp.Text);
            Assert.StartsWith("Aadhaar ", rp.Lines[0].Text);
            Assert.Equal(page.Lines[0].Text.Length, rp.Lines[0].Text.Length);
            Assert.DoesNotContain(rp.Text, c => char.IsDigit(c));
            Assert.Empty(Redactor.Find(rp));                      // nothing left to find
            Assert.Equal(page.RotationApplied, rp.RotationApplied);
            Assert.Equal(page.SourceDpi, rp.SourceDpi);
        }
    }

    [Fact]
    public void Apply_only_touches_the_selected_findings()
    {
        var page = PageOf($"PAN {Pan}", $"Ph {Phone}");
        using var img = GrayPage(page.Width, page.Height);
        var all = Redactor.Find(page);
        var phone = all.Single(f => f.Kind == PiiKind.Phone);
        var (red, rp) = Redactor.Apply(img, page, new[] { phone });
        using (red)
        {
            Assert.Contains(Pan, rp.Text);
            Assert.DoesNotContain(Phone, rp.Text);
            var pan = all.Single(f => f.Kind == PiiKind.Pan);
            Assert.Equal(new SKColor(200, 200, 200, 255), red.GetPixel((int)(pan.Box.Left + pan.Box.Width / 2), (int)(pan.Box.Top + pan.Box.Height / 2)));
        }
    }

    [Fact]
    public void Apply_rejects_a_bitmap_that_does_not_match_the_page_size_instead_of_scaling()
    {
        var page = PageOf($"Ph {Phone}");
        var f = Assert.Single(Redactor.Find(page));
        foreach (var (w, h) in new[] { (page.Width / 2, page.Height / 2), (page.Height, page.Width), (page.Width + 2, page.Height), (page.Width, page.Height - 5), (1, 1) })
        {
            using var img = GrayPage(w, h);
            var ex = Assert.Throws<ArgumentException>(() => Redactor.Apply(img, page, new[] { f }));
            Assert.DoesNotContain(Phone, ex.Message);
        }
        using var one = GrayPage(page.Width + 1, page.Height - 1); // within one pixel is fine
        var (red, _) = Redactor.Apply(one, page, new[] { f });
        red.Dispose();
    }

    [Fact]
    public void MaskText_masks_the_text_without_an_image_and_matches_what_Apply_returns()
    {
        var a = Aadhaar("23456789012");
        var page = PageOf($"Aadhaar {Group(a)}", "nothing here", $"PAN {Pan} Ph {Phone}");
        var fs = Redactor.Find(page);
        var masked = Redactor.MaskText(page, fs);
        Assert.Equal("Aadhaar XXXXXXXXXXXXXX", masked.Lines[0].Text);
        Assert.Equal("nothing here", masked.Lines[1].Text);
        Assert.DoesNotContain(Pan, masked.Text);
        Assert.DoesNotContain(Phone, masked.Text);
        Assert.Contains(a[8..], page.Text);   // input untouched
        using var img = GrayPage(page.Width, page.Height);
        var (red, viaApply) = Redactor.Apply(img, page, fs);
        red.Dispose();
        Assert.Equal(viaApply.Text, masked.Text);
        Assert.Equal("YYYYYYYYYY", Redactor.MaskText(PageOf(Phone), Redactor.Find(PageOf(Phone)), 'Y').Text);
        Assert.Throws<ArgumentException>(() => Redactor.MaskText(page, fs, '5'));
        var bad = new PiiFinding(PiiKind.Phone, 9, 0, 3, "X", new RectangleF(), new PointF[4]);
        var ex = Assert.Throws<ArgumentException>(() => Redactor.MaskText(page, new[] { bad }));
        Assert.DoesNotContain(Phone, ex.Message);
    }

    [Fact]
    public void Apply_rejects_findings_that_do_not_fit_the_page_without_echoing_text()
    {
        var page = PageOf($"Ph {Phone}");
        using var img = GrayPage(page.Width, page.Height);
        var bad = new PiiFinding(PiiKind.Phone, 5, 0, 3, "X", new RectangleF(), new PointF[4]);
        var ex = Assert.Throws<ArgumentException>(() => Redactor.Apply(img, page, new[] { bad }));
        Assert.DoesNotContain(Phone, ex.Message);
        Assert.Throws<ArgumentException>(() => Redactor.Apply(img, page, Redactor.Find(page), '7'));
    }

    static SKBitmap Jpeg(SKBitmap b, int quality = 85)
    {
        using var im = SKImage.FromBitmap(b);
        using var d = im.Encode(SKEncodedImageFormat.Jpeg, quality);
        return SKBitmap.Decode(d.ToArray());
    }

    [Fact]
    public void Redacted_image_has_no_alpha_and_keeps_no_original_pixels_after_a_real_jpeg_reencode()
    {
        var page = PageOf($"Ph {Phone}");
        using var img = GrayPage(page.Width, page.Height);
        using (var c = new SKCanvas(img)) c.DrawRect(0, 0, page.Width, page.Height, new SKPaint { Color = SKColors.Red });
        var (red, _, fs) = Redactor.RedactAll(img, page);
        using (red)
        {
            Assert.Equal(SKAlphaType.Opaque, red.AlphaType);
            var f = Assert.Single(fs);
            using var jpg = Jpeg(red, 60);   // what the searchable PDF embeds, at a harsher quality
            int n = 0;
            for (int y = (int)f.Box.Top + 4; y < f.Box.Bottom - 4; y += 3)
                for (int x = (int)f.Box.Left + 4; x < f.Box.Right - 4; x += 3)
                {
                    var p = jpg.GetPixel(x, y);
                    Assert.True(p.Red < 40 && p.Green < 40 && p.Blue < 40, $"pixel {x},{y} is {p}");   // no trace of the red original
                    n++;
                }
            Assert.True(n > 100);
            Assert.True(jpg.GetPixel(2, 2).Red > 200);   // outside the box the red page is still there
        }
    }

    // ---- Rendered pages ----------------------------------------------------------------------------------------------------------

    /// <summary>A piece of a rendered line. Secret pieces are the personal data: they are also drawn alone on a second bitmap (the ink map).</summary>
    sealed record Seg(string Text, bool Secret = false, string? Font = null, float? Size = null, float Gap = 0, bool NewOcrLine = false);

    static string FontFor(string text, string? forced)
    {
        if (forced != null) return forced;
        foreach (char c in text)
        {
            if (c >= 0x0C00 && c <= 0x0C7F) return "NotoSansTelugu-Regular.ttf";
            if (c >= 0x0900 && c <= 0x097F) return "NotoSansDevanagari-Regular.ttf";
        }
        return "NotoSans-Regular.ttf";
    }

    /// <summary>
    /// Draws lines of segments with HarfBuzz shaping and returns the image, an ink map of the secret segments only (black on white),
    /// and the page with one OCR-like line per drawn line (the box is the real extent of the drawn text).
    /// </summary>
    static (SKBitmap Img, SKBitmap Ink, OcrPage Page) RenderPage(IReadOnlyList<Seg[]> lines, int width, int height, float size, float left = 100, float lineHeight = 70)
    {
        var img = new SKBitmap(width, height); var ink = new SKBitmap(width, height);
        using var c1 = new SKCanvas(img); using var c2 = new SKCanvas(ink);
        c1.Clear(SKColors.White); c2.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        var ocr = new List<OcrLine>();
        float top = 40;
        foreach (var segs in lines)
        {
            float x = left, baseline = top + size * 1.05f, lineStart = left;
            var text = new StringBuilder();
            void Flush()
            {
                if (text.Length == 0) return;
                var box = new RectangleF(lineStart, top, x - lineStart, lineHeight);
                ocr.Add(new OcrLine(text.ToString(), 0.9f, OcrLanguage.English, box, new[] { new PointF(box.Left, box.Top), new PointF(box.Right, box.Top), new PointF(box.Right, box.Bottom), new PointF(box.Left, box.Bottom) }));
                text.Clear();
            }
            foreach (var sg in segs)
            {
                if (sg.NewOcrLine) Flush();
                x += sg.Gap;
                if (text.Length == 0) lineStart = x;
                // split into Latin and Indic runs so each run has real glyphs
                int i = 0;
                while (i < sg.Text.Length)
                {
                    bool latin = sg.Text[i] < 0x0900; int j = i;
                    while (j < sg.Text.Length && (sg.Text[j] < 0x0900) == latin) j++;
                    var run = sg.Text[i..j];
                    using var tf = SKTypeface.FromFile(TestEnv.Font(latin ? (sg.Font ?? "NotoSans-Regular.ttf") : FontFor(run, null)));
                    using var font = new SKFont(tf, sg.Size ?? size);
                    c1.DrawShapedText(run, x, baseline, SKTextAlign.Left, font, paint);
                    if (sg.Secret) c2.DrawShapedText(run, x, baseline, SKTextAlign.Left, font, paint);
                    using var shaper = new SkiaSharp.HarfBuzz.SKShaper(tf);
                    x += shaper.Shape(run, font).Width;
                    i = j;
                }
                text.Append(sg.Text);
            }
            Flush();
            top += lineHeight + 20;
        }
        return (img, ink, new OcrPage(0, width, height, ocr, TimeSpan.Zero));
    }

    /// <summary>Counts pixels with ANY ink coverage in the map (anti-aliased fringes included) that are not near-black in <paramref name="shown"/> (the redacted image as exported).</summary>
    static (int Ink, int Leaked) LeakedInkPixels(SKBitmap ink, SKBitmap shown)
    {
        int n = 0, leak = 0;
        for (int y = 0; y < ink.Height; y++)
            for (int x = 0; x < ink.Width; x++)
            {
                if (ink.GetPixel(x, y).Red > 250) continue;
                n++;
                var p = shown.GetPixel(x, y);
                if (p.Red > 90 || p.Green > 90 || p.Blue > 90) leak++;
            }
        return (n, leak);
    }

    /// <summary>The first JPEG stream embedded in a PDF written by SearchablePdfExporter.</summary>
    static SKBitmap ExtractEmbeddedJpeg(byte[] pdf)
    {
        int start = pdf.AsSpan().IndexOf(new byte[] { 0xFF, 0xD8, 0xFF });
        Assert.True(start >= 0, "no JPEG in the PDF");
        int end = pdf.AsSpan(start).IndexOf(Encoding.ASCII.GetBytes("endstream"));
        Assert.True(end > 0);
        var jpeg = pdf.AsSpan(start, end).ToArray();
        int eoi = jpeg.Length;
        while (eoi >= 2 && !(jpeg[eoi - 2] == 0xFF && jpeg[eoi - 1] == 0xD9)) eoi--;
        Assert.True(eoi >= 2);
        return SKBitmap.Decode(jpeg.AsSpan(0, eoi).ToArray());
    }

    // ---- Exports contain no original value -------------------------------------------------------------------------------------

    static (string Ascii, string Telugu, string Dev) Variants(string asciiDigits) =>
        (asciiDigits, Mask(asciiDigits, "౦"), Mask(asciiDigits, "०"));

    /// <summary>A rendered page (digits really drawn in the image) with six personal values on it, already redacted.</summary>
    static (SKBitmap Original, SKBitmap Ink, SKBitmap Img, OcrPage Page, string[] Secrets) SecretPage()
    {
        var a = Aadhaar("23456789012");
        var a2 = Aadhaar("98765432101");
        var secrets = new List<string>();
        foreach (var d in new[] { a, a2, Phone })
        {
            var (x, t, h) = Variants(d);
            secrets.AddRange(new[] { x, t, h });
        }
        secrets.AddRange(new[] { Group(a), Group(a2), a[..4] + "-" + a[4..8] + "-" + a[8..], Pan, "+91 98765 43210", "98765 43210" });
        var lines = new List<Seg[]>
        {
            new[] { new Seg("Aadhaar "), new Seg(Group(a), true) },
            new[] { new Seg("ఆధార్ "), new Seg(Mask(a, "౦"), true) },
            new[] { new Seg("UID "), new Seg(Mask(a2, "०"), true) },
            new[] { new Seg("PAN "), new Seg(Pan, true) },
            new[] { new Seg("ఫోన్ "), new Seg("+91 98765 43210", true) },
            new[] { new Seg("Mob "), new Seg(Mask(Phone, "౦"), true) },
            new[] { new Seg("Plain line stays") },
        };
        var (orig, ink, page) = RenderPage(lines, 1400, 700, 40);
        var (red, rp, fs) = Redactor.RedactAll(orig, page);
        Assert.Equal(6, fs.Count);
        return (orig, ink, red, rp, secrets.ToArray());
    }

    static void AssertNoSecret(string where, string haystack, string[] secrets)
    {
        var h = haystack.Replace(" ", "").Replace("-", "");
        foreach (var s in secrets)
            Assert.False(h.Contains(s.Replace(" ", "").Replace("-", ""), StringComparison.Ordinal), $"{where} still contains a secret ({s.Length} chars)");
    }

    static void AssertNoSecretBytes(string where, byte[] bytes, string[] secrets)
    {
        foreach (var s in secrets)
            foreach (var enc in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
            {
                var needle = enc.GetBytes(s);
                Assert.True(bytes.AsSpan().IndexOf(needle) < 0, $"{where} still contains a secret ({s.Length} chars) as {enc.WebName}");
            }
    }

    [FontsFact]
    public void Redacted_txt_and_docx_hold_none_of_the_original_numbers()
    {
        var (orig, ink, img, page, secrets) = SecretPage();
        using (orig) using (ink) using (img)
        {
            var dir = Path.Combine(Path.GetTempPath(), "redact-" + Guid.NewGuid().ToString("N"));
            try
            {
                var txt = Path.Combine(dir, "o.txt"); var docx = Path.Combine(dir, "o.docx");
                TextExporter.Write(new[] { page }, txt);
                DocxExporter.Write(new[] { page }, docx);
                var txtBytes = File.ReadAllBytes(txt);
                AssertNoSecretBytes("txt", txtBytes, secrets);
                AssertNoSecret("txt text", Encoding.UTF8.GetString(txtBytes), secrets);
                Assert.Contains("Plain line stays", Encoding.UTF8.GetString(txtBytes));

                // docx: every part of the zip, raw bytes and decoded
                using var zip = ZipFile.OpenRead(docx);
                foreach (var e in zip.Entries)
                {
                    using var ms = new MemoryStream(); using (var s = e.Open()) s.CopyTo(ms);
                    AssertNoSecretBytes("docx/" + e.FullName, ms.ToArray(), secrets);
                }
                var xml = new StreamReader(zip.GetEntry("word/document.xml")!.Open()).ReadToEnd();
                AssertNoSecret("document.xml", xml, secrets);
                Assert.Contains("Plain line stays", xml);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    [PdftotextFact]
    public void Redacted_searchable_pdf_has_no_original_numbers_in_bytes_or_extracted_text()
    {
        var (orig, ink, img, page, secrets) = SecretPage();
        using (orig) using (ink) using (img)
        {
            var dir = Path.Combine(Path.GetTempPath(), "redact-" + Guid.NewGuid().ToString("N"));
            try
            {
                var pdf = Path.Combine(dir, "o.pdf");
                SearchablePdfExporter.Write(new[] { (img, page) }, pdf, TestEnv.FontsDir);
                AssertNoSecretBytes("pdf", File.ReadAllBytes(pdf), secrets);

                var psi = new System.Diagnostics.ProcessStartInfo("pdftotext", $"-enc UTF-8 \"{pdf}\" -") { RedirectStandardOutput = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                var text = p.StandardOutput.ReadToEnd(); p.WaitForExit();
                _out.WriteLine(text);
                AssertNoSecret("pdftotext", text, secrets);
                Assert.Contains("Plain line stays", text);
                Assert.Contains("XXXX", text);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    [FontsFact]
    public void The_image_embedded_in_the_exported_pdf_shows_no_secret_ink()
    {
        var (orig, ink, img, page, secrets) = SecretPage();
        using (orig) using (ink) using (img)
        {
            // sanity: the original really shows the digits (so "covered" below means something)
            var (n0, leak0) = LeakedInkPixels(ink, orig);
            Assert.True(n0 > 2000, $"only {n0} ink pixels were drawn");
            Assert.True(leak0 > 0, "the anti-aliased edges of the digits are visible on the unredacted page");
            int whiteBecameBlack = 0;
            for (int y = 0; y < orig.Height; y += 2)
                for (int x = 0; x < orig.Width; x += 2)
                    if (orig.GetPixel(x, y).Red > 240 && img.GetPixel(x, y).Red < 10) whiteBecameBlack++;
            Assert.True(whiteBecameBlack > 1000, "the redaction painted over the page");

            var dir = Path.Combine(Path.GetTempPath(), "redact-" + Guid.NewGuid().ToString("N"));
            try
            {
                var pdf = Path.Combine(dir, "o.pdf");
                SearchablePdfExporter.Write(new[] { (img, page) }, pdf, TestEnv.FontsDir);
                using var embedded = ExtractEmbeddedJpeg(File.ReadAllBytes(pdf));
                Assert.Equal(img.Width, embedded.Width);
                var (n, leak) = LeakedInkPixels(ink, embedded);
                _out.WriteLine($"secret ink pixels {n}, visible in the exported JPEG {leak}");
                Assert.Equal(0, leak);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    [FontsFact]
    public void A_big_number_with_wide_gaps_is_covered_completely()
    {
        var a = Aadhaar("23456789012");
        var lines = new List<Seg[]>
        {
            new[]
            {
                new Seg("AADHAAR", false, "NotoSans-Bold.ttf"),
                new Seg(a[..4], true, "NotoSans-Bold.ttf", null, 90, true),
                new Seg(a[4..8], true, "NotoSans-Bold.ttf", null, 130, true),
                new Seg(a[8..], true, "NotoSans-Bold.ttf", null, 130, true),
            },
        };
        var (orig, ink, page) = RenderPage(lines, 2400, 200, 64, lineHeight: 100);
        using (orig) using (ink)
        {
            var fs = Redactor.Find(page);
            Assert.Equal(4, page.Lines.Count);
            Assert.Equal(3, fs.Count);
            Assert.All(fs, f => Assert.Equal(PiiKind.Aadhaar, f.Kind));
            var (red, _) = Redactor.Apply(orig, page, fs);
            using (red) { var (n, leak) = LeakedInkPixels(ink, red); Assert.True(n > 1000); Assert.Equal(0, leak); }
        }
    }

    // ---- End to end with the real models ---------------------------------------------------------------------------------------

    [ModelsFact]
    public void End_to_end_Telugu_English_page_is_found_redacted_and_unreadable_afterwards()
    {
        var a = Aadhaar("23456789012");
        var lines = new (string, string)[]
        {
            ("ఆధార్ సంఖ్య: " + Group(a), "NotoSansTelugu-Regular.ttf"),
            ("PAN: " + Pan, "NotoSansTelugu-Regular.ttf"),
            ("ఫోన్ నంబర్: +91 98765 43210", "NotoSansTelugu-Regular.ttf"),
            ("తెలుగు భాష చాలా మధురమైనది", "NotoSansTelugu-Regular.ttf"),
        };
        using var bmp = TestEnv.RenderMixed(lines, 1400, 520, 44);
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var langs = new[] { OcrLanguage.Telugu, OcrLanguage.English };
        var page = eng.Recognize(bmp, langs);
        _out.WriteLine("lines: " + page.Lines.Count);

        var findings = Redactor.Find(page);
        foreach (var f in findings) _out.WriteLine($"{f.Kind} line {f.LineIndex} [{f.Start}+{f.Length}] {f.MaskedText} box {f.Box}");
        Assert.Contains(findings, f => f.Kind == PiiKind.Aadhaar);
        Assert.Contains(findings, f => f.Kind == PiiKind.Pan);
        Assert.Contains(findings, f => f.Kind == PiiKind.Phone);

        var (red, rp) = Redactor.Apply(bmp, page, findings);
        using (red)
        {
            Assert.DoesNotContain("4567", rp.Text.Replace(" ", ""));
            Assert.DoesNotContain(Pan, rp.Text);

            // Re-OCR the redacted pixels: none of the three values may come back, even partially (8+ digits / the PAN).
            using var jpg = Jpeg(red);   // as embedded in the searchable PDF
            var again = eng.Recognize(jpg, langs);
            var all = again.Text.Replace(" ", "").Replace("-", "");
            _out.WriteLine("after: " + again.Text.Replace("\n", " | "));
            Assert.DoesNotContain(a[..6], all);
            Assert.DoesNotContain(a[6..], all);
            Assert.DoesNotContain("1234F", all);
            Assert.DoesNotContain("ABCPE", all);
            Assert.DoesNotContain("98765", all);
            Assert.DoesNotContain("43210", all);
            Assert.Empty(Redactor.Find(again));
            // the harmless line is still readable
            Assert.Contains(again.Lines, l => TestEnv.Cer("తెలుగు భాష చాలా మధురమైనది", l.Text) < 0.15);
        }
    }

    [ModelsFact]
    public void Big_bold_aadhaar_with_wide_group_gaps_is_found_redacted_and_gone_after_a_jpeg_and_a_second_ocr()
    {
        var a = Aadhaar("23456789012");
        var lines = new List<Seg[]>
        {
            new[] { new Seg("AADHAAR   ", false, "NotoSans-Bold.ttf"), new Seg($"{a[..4]}    {a[4..8]}    {a[8..]}", true, "NotoSans-Bold.ttf") },
            new[] { new Seg("Mobile: "), new Seg("98490      12345", true) },
        };
        var (orig, ink, _) = RenderPage(lines, 2480, 340, 64, lineHeight: 100);
        using (orig) using (ink)
        {
            using var eng = new OcrEngine(TestEnv.ModelsDir!);
            var langs = new[] { OcrLanguage.English };
            var page = eng.Recognize(orig, langs);
            foreach (var l in page.Lines) _out.WriteLine($"OCR line ({l.Box.Width:0}x{l.Box.Height:0}) {l.Text.Length} chars");
            var fs = Redactor.Find(page);
            foreach (var f in fs) _out.WriteLine($"{f.Kind} line {f.LineIndex} verified={f.Verified} group={f.GroupId} {f.MaskedText}");
            Assert.Contains(fs, f => f.Kind == PiiKind.Aadhaar);
            Assert.Contains(fs, f => f.Kind == PiiKind.Phone);
            var (red, rp) = Redactor.Apply(orig, page, fs);
            using (red)
            {
                var (n, leak) = LeakedInkPixels(ink, red);
                _out.WriteLine($"secret ink pixels {n}, uncovered {leak}");
                Assert.Equal(0, leak);
                using var jpg = Jpeg(red);
                var again = eng.Recognize(jpg, langs);
                var all = again.Text.Replace(" ", "");
                _out.WriteLine("after: " + again.Lines.Count + " lines");
                foreach (var part in new[] { a[..4], a[4..8], a[8..], "98490", "12345" }) Assert.DoesNotContain(part, all);
                Assert.Empty(Redactor.Find(again));
                Assert.DoesNotContain(a[8..], rp.Text);
            }
        }
    }

    [ModelsFact]
    public void Redacted_pdf_image_re_ocr_finds_no_personal_digits()
    {
        var (orig, ink, img, page, secrets) = SecretPage();
        using (orig) using (ink) using (img)
        {
            var dir = Path.Combine(Path.GetTempPath(), "redact-" + Guid.NewGuid().ToString("N"));
            try
            {
                var pdf = Path.Combine(dir, "o.pdf");
                SearchablePdfExporter.Write(new[] { (img, page) }, pdf, TestEnv.FontsDir);
                using var embedded = ExtractEmbeddedJpeg(File.ReadAllBytes(pdf));
                using var eng = new OcrEngine(TestEnv.ModelsDir!);
                var again = eng.Recognize(embedded, new[] { OcrLanguage.Telugu, OcrLanguage.English });
                var flat = PiiDetector.NormalizeDigits(again.Text).Replace(" ", "").Replace("-", "");
                _out.WriteLine("after: " + again.Text.Replace("\n", " | ").Length + " chars");
                foreach (var secret in new[] { "2345678", "9876543", "98765", "43210", "ABCPE", "1234F" })
                    Assert.DoesNotContain(secret, flat);
                Assert.Empty(Redactor.Find(again));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    /// <summary>
    /// MEASUREMENT, not a promise. The OCR models rarely read numbers printed in Indian-language digits correctly, so the detector
    /// often cannot see them. This renders pages with Aadhaar and phone numbers in Telugu and Devanagari digits (and the same
    /// pages with ASCII digits as a control), runs the real OCR and prints how many numbers were detected.
    /// </summary>
    [ModelsFact]
    public void Detection_rate_for_numbers_written_in_indic_digits_is_measured_and_reported()
    {
        var payloads = new[] { "23456789012", "34567890123", "45678901234", "56789012345", "67890123456", "98765432101" };
        var phones = new[] { "9876543210", "9849012345", "8123456789", "7012345678", "9000012345", "6301234567" };
        using var eng = new OcrEngine(TestEnv.ModelsDir!);
        var results = new List<string>();
        double controlRate = 0;
        foreach (var (name, zero, label, lang) in new[]
        {
            ("Telugu digits", "౦", "ఆధార్ సంఖ్య: ", OcrLanguage.Telugu),
            ("Devanagari digits", "०", "आधार संख्या: ", OcrLanguage.Hindi),
            ("ASCII digits (control)", "0", "Aadhaar No: ", OcrLanguage.English),
        })
        {
            int found = 0, total = 0;
            for (int i = 0; i < payloads.Length; i++)
            {
                var a = Aadhaar(payloads[i]);
                string Digits(string d) => zero == "0" ? d : Mask(d, zero);
                var lines = new List<Seg[]>
                {
                    new[] { new Seg(label), new Seg($"{Digits(a[..4])} {Digits(a[4..8])} {Digits(a[8..])}", true) },
                    new[] { new Seg(zero == "0" ? "Mobile: " : (zero == "౦" ? "ఫోన్: " : "मोबाइल: ")), new Seg($"{Digits(phones[i][..5])} {Digits(phones[i][5..])}", true) },
                };
                var (img, ink, _) = RenderPage(lines, 1500, 260, 46);
                using (img) using (ink)
                {
                    var page = eng.Recognize(img, lang == OcrLanguage.English ? new[] { OcrLanguage.English } : new[] { lang, OcrLanguage.English });
                    var fs = Redactor.Find(page);
                    total += 2;
                    if (fs.Any(f => f.Kind == PiiKind.Aadhaar)) found++;
                    if (fs.Any(f => f.Kind == PiiKind.Phone)) found++;
                }
            }
            double rate = (double)found / total;
            if (zero == "0") controlRate = rate;
            results.Add($"{name}: {found}/{total} numbers detected ({rate:P0})");
        }
        foreach (var r in results) _out.WriteLine("DETECTION " + r);
        // The control proves the harness works; the Indic-digit rates are only reported (see Redactor docs for the user-facing warning).
        Assert.True(controlRate >= 0.8, $"control (ASCII digits) detection rate {controlRate:P0}");
    }
}

static class RectExt
{
    public static float MidX(this RectangleF r) => r.Left + r.Width / 2;
    public static float MidY(this RectangleF r) => r.Top + r.Height / 2;
}
