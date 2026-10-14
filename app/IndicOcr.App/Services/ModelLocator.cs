using IndicOcr.Core;

namespace IndicOcr.App.Services;

internal sealed record ModelSearchResult(string? Directory, IReadOnlyList<string> Searched);

internal static class ModelLocator
{
    /// <summary>
    /// Order: --models arg, INDICOCR_MODELS env, models next to the exe, %LOCALAPPDATA%\IndicOcr\models.
    /// A directory counts as found only if it contains at least one usable language.
    /// </summary>
    public static ModelSearchResult Resolve(string[] commandLine)
    {
        var candidates = new List<string>();

        for (int i = 0; i < commandLine.Length; i++)
        {
            string a = commandLine[i];
            if (a.Equals("--models", StringComparison.OrdinalIgnoreCase) && i + 1 < commandLine.Length)
            {
                candidates.Add(commandLine[i + 1]);
            }
            else if (a.StartsWith("--models=", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(a.Substring("--models=".Length).Trim('"'));
            }
        }

        string? env = Environment.GetEnvironmentVariable("INDICOCR_MODELS");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);

        candidates.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "models"));
        candidates.Add(System.IO.Path.Combine(AppPaths.DataDirectory, "models"));

        foreach (string c in candidates)
        {
            if (HasModels(c)) return new ModelSearchResult(c, candidates);
        }
        return new ModelSearchResult(null, candidates);
    }

    private static bool HasModels(string dir)
    {
        try
        {
            return System.IO.Directory.Exists(dir) && OcrEngine.AvailableLanguages(dir).Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
