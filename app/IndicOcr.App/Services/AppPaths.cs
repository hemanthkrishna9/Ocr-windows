namespace IndicOcr.App.Services;

internal static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\IndicOcr (unpackaged apps cannot use ApplicationData).</summary>
    public static string DataDirectory =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IndicOcr");
}
