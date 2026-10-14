using System.Buffers.Binary;

namespace IndicOcr.Core.Internal;

/// <summary>Reads the resolution stored in PNG (pHYs), JPEG (JFIF) and BMP headers. Returns null when absent or implausible.</summary>
internal static class ImageDpi
{
    /// <summary>Values below 100 dpi (72 and 96 are placeholders written by editors and screenshots) or above 1200 are treated as unknown.</summary>
    internal static int? Sane(double dpi) => dpi >= 100 && dpi <= 1200 ? (int)Math.Round(dpi) : null;

    public static int? TryRead(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[64 * 1024];
            int n = fs.Read(buf, 0, buf.Length);
            return Parse(buf.AsSpan(0, n));
        }
        catch { return null; }
    }

    internal static int? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return Png(d);
        if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8) return Jpeg(d);
        if (d.Length > 42 && d[0] == 'B' && d[1] == 'M')
        {
            int ppm = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(38, 4));
            return ppm > 0 ? Sane(ppm * 0.0254) : null;
        }
        return null;
    }

    static int? Png(ReadOnlySpan<byte> d)
    {
        int p = 8;
        while (p + 12 <= d.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(p, 4));
            var type = d.Slice(p + 4, 4);
            if (type.SequenceEqual("pHYs"u8) && len >= 9 && p + 8 + 9 <= d.Length)
            {
                uint ppu = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(p + 8, 4));
                byte unit = d[p + 16];
                return unit == 1 && ppu > 0 ? Sane(ppu * 0.0254) : null;
            }
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8)) return null; // pHYs must precede IDAT
            long next = (long)p + 12 + len;
            if (next > d.Length) return null;
            p = (int)next;
        }
        return null;
    }

    static int? Jpeg(ReadOnlySpan<byte> d)
    {
        int p = 2;
        while (p + 4 <= d.Length && d[p] == 0xFF)
        {
            byte marker = d[p + 1];
            if (marker == 0xDA || marker == 0xD9) return null;
            int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 2, 2));
            if (marker == 0xE0 && len >= 16 && p + 4 + 12 <= d.Length && d.Slice(p + 4, 5).SequenceEqual("JFIF\0"u8))
            {
                byte units = d[p + 11];
                int xd = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 12, 2));
                if (units == 1) return Sane(xd);
                if (units == 2) return Sane(xd * 2.54);
                return null;
            }
            p += 2 + len;
        }
        return null;
    }
}
