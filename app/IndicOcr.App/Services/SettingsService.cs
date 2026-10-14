using System.Text.Json;
using IndicOcr.Core;

namespace IndicOcr.App.Services;

internal sealed class AppSettings
{
    /// <summary>
    /// With Auto-detect on, the ticked Indic languages only restrict which scripts may be chosen (none ticked = all installed).
    /// Without it, they are the languages that are read.
    /// </summary>
    public List<string> Languages { get; set; } = new() { nameof(OcrLanguage.Telugu), nameof(OcrLanguage.English) };

    /// <summary>
    /// Null when the setting is missing (a settings.json written before Auto existed): Load turns that into false so an existing
    /// install keeps its explicit language choice. A new install gets true from <see cref="SettingsService.Fresh"/>.
    /// </summary>
    public bool? AutoDetect { get; set; }

    /// <summary>"Redact ID numbers" in the Export menu. Off by default; the choice is remembered.</summary>
    public bool RedactIds { get; set; }

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
                if (s?.Languages != null)
                {
                    s.AutoDetect ??= false; // existing install: keep the explicit language choice
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Settings load failed", ex);
        }
        var fresh = Fresh();
        if (!File.Exists(FilePath)) Save(fresh); // write the defaults once so the Hotkey setting is easy to find
        return fresh;
    }

    /// <summary>New install: Auto-detect on, no language restriction.</summary>
    internal static AppSettings Fresh() => new() { AutoDetect = true, Languages = new List<string>() };

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
