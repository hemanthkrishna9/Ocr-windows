namespace IndicOcr.Core.Internal;

/// <summary>Verhoeff checksum (the check Aadhaar and VID numbers carry in their last digit).</summary>
internal static class PiiVerhoeff
{
    static readonly byte[,] D =
    {
        {0,1,2,3,4,5,6,7,8,9}, {1,2,3,4,0,6,7,8,9,5}, {2,3,4,0,1,7,8,9,5,6}, {3,4,0,1,2,8,9,5,6,7}, {4,0,1,2,3,9,5,6,7,8},
        {5,9,8,7,6,0,4,3,2,1}, {6,5,9,8,7,1,0,4,3,2}, {7,6,5,9,8,2,1,0,4,3}, {8,7,6,5,9,3,2,1,0,4}, {9,8,7,6,5,4,3,2,1,0},
    };

    static readonly byte[,] P =
    {
        {0,1,2,3,4,5,6,7,8,9}, {1,5,7,6,2,8,3,0,9,4}, {5,8,0,3,7,9,6,1,4,2}, {8,9,1,6,0,4,3,5,2,7},
        {9,4,5,3,1,2,6,8,7,0}, {4,2,8,6,5,7,3,9,0,1}, {2,7,9,3,8,0,6,4,1,5}, {7,0,4,6,9,1,3,2,5,8},
    };

    static readonly byte[] Inv = { 0, 4, 3, 2, 1, 5, 6, 7, 8, 9 };

    /// <summary>True when the ASCII digit string (check digit included) passes the Verhoeff test. Non-digits make it false.</summary>
    public static bool IsValid(string digits)
    {
        if (digits.Length < 2) return false;
        int c = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[digits.Length - 1 - i] - '0';
            if (d < 0 || d > 9) return false;
            c = D[c, P[i % 8, d]];
        }
        return c == 0;
    }

    /// <summary>The check digit to append to <paramref name="payload"/> (ASCII digits) so the result is valid.</summary>
    public static char CheckDigit(string payload)
    {
        int c = 0;
        for (int i = 0; i < payload.Length; i++)
        {
            int d = payload[payload.Length - 1 - i] - '0';
            c = D[c, P[(i + 1) % 8, d]];
        }
        return (char)('0' + Inv[c]);
    }
}
