namespace IndicOcr.Core;

public static class InputFiles
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".pdf" };

    /// <summary>Supported input file extensions (lower case, with the dot), for building a file picker filter.</summary>
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        new[] { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".pdf" };

    public static bool IsSupported(string path) =>
        !string.IsNullOrEmpty(path) && Extensions.Contains(Path.GetExtension(path));

    /// <summary>A supported file yields itself; a folder yields its supported files in natural name order.</summary>
    public static IEnumerable<string> Enumerate(string fileOrFolder, bool recursive)
    {
        if (File.Exists(fileOrFolder))
        {
            if (IsSupported(fileOrFolder)) yield return fileOrFolder;
            yield break;
        }
        if (!Directory.Exists(fileOrFolder)) yield break;
        var opt = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = recursive };
        var files = Directory.EnumerateFiles(fileOrFolder, "*", opt).Where(IsSupported).ToList();
        files.Sort(NaturalComparer.Instance);
        foreach (var f in files) yield return f;
    }

    sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();
        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a.AsSpan(si, i - si).TrimStart('0');
                    var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    int c = na.CompareTo(nb, StringComparison.Ordinal);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
