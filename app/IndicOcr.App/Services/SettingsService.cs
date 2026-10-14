using System.Text.Json;
using IndicOcr.Core;

namespace IndicOcr.App.Services;

internal sealed class AppSettings
{
    public List<string> Languages { get; set; } = new() { nameof(OcrLanguage.Telugu), nameof(OcrLanguage.English) };

    /// <summary>Global snip hotkey, e.g. "Win+Shift+O". Modifiers: Ctrl, Alt, Shift, Win. Edit settings.json and restart to change.</summary>
    public string Hotkey { get; set; } = HotkeyService.DefaultSpec;
}

internal static class SettingsService
{
    private static string FilePath => System.IO.Path.Combine(AppPaths.DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s?.Languages != null) return s;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Settings load failed", ex);
        }
        var fresh = new AppSettings();
        if (!File.Exists(FilePath)) Save(fresh); // write the defaults once so the Hotkey setting is easy to find
        return fresh;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Error("Settings save failed", ex);
        }
    }
}
