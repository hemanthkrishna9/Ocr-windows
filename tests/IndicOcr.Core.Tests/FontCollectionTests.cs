using Xunit;

namespace IndicOcr.Core.Tests;

public class FontCollectionTests
{
    static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
    static void Put32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    [Fact]
    public void SfntFace_rebuilds_a_standalone_font_from_a_ttc()
    {
        // synthetic font: 2 tables, wrapped as a 1-face collection (table offsets are absolute in the file)
        var ttf = new byte[12 + 32 + 8 + 5];
        Put32(ttf, 0, 0x00010000); ttf[5] = 2;
        "abcd"u8.CopyTo(ttf.AsSpan(12)); Put32(ttf, 12 + 8, 44); Put32(ttf, 12 + 12, 8);
        "wxyz"u8.CopyTo(ttf.AsSpan(28)); Put32(ttf, 28 + 8, 52); Put32(ttf, 28 + 12, 5);
        for (int i = 44; i < ttf.Length; i++) ttf[i] = (byte)i;
        var ttc = new byte[16 + ttf.Length];
        "ttcf"u8.CopyTo(ttc); Put32(ttc, 4, 0x00010000); Put32(ttc, 8, 1); Put32(ttc, 12, 16);
        ttf.CopyTo(ttc, 16);
        for (int t = 0; t < 2; t++) Put32(ttc, 16 + 12 + 16 * t + 8, U32(ttf, 12 + 16 * t + 8) + 16);

        var face = PdfFonts.SfntFace(ttc, 0);
        Assert.Equal(0x00010000u, U32(face, 0));
        uint off0 = U32(face, 12 + 8), len0 = U32(face, 12 + 12), off1 = U32(face, 28 + 8), len1 = U32(face, 28 + 12);
        Assert.Equal(ttf.AsSpan(44, 8).ToArray(), face.AsSpan((int)off0, (int)len0).ToArray());
        Assert.Equal(ttf.AsSpan(52, 5).ToArray(), face.AsSpan((int)off1, (int)len1).ToArray());
        Assert.Same(ttf, PdfFonts.SfntFace(ttf, 0)); // plain fonts pass through
    }

    [Fact]
    public void Supported_extensions_are_listed()
    {
        Assert.Contains(".pdf", InputFiles.SupportedExtensions);
        Assert.Contains(".tif", InputFiles.SupportedExtensions);
    }
}
