using System.Diagnostics;
using SkiaSharp;

namespace IndicOcr.App.Services;

/// <summary>
/// Per-session folder under %LOCALAPPDATA%\IndicOcr\cache holding each OCR'd page as a JPEG, so the
/// full-resolution bitmaps do not stay in memory. The folder is deleted on exit and on Clear queue;
/// folders left by a crashed session are removed at the next start.
/// </summary>
internal static class PageCache
{
    private static readonly object Gate = new();
    private static string? _sessionDir;

    private static string Root => System.IO.Path.Combine(AppPaths.DataDirectory, "cache");

    private static string SessionDir
    {
        get
        {
            lock (Gate)
            {
                if (_sessionDir == null)
                {
                    _sessionDir = System.IO.Path.Combine(Root, "s-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_sessionDir);
                }
                return _sessionDir;
            }
        }
    }

    /// <summary>Encodes the bitmap as JPEG into the session folder and returns the file path. Any thread.</summary>
    public static string Save(SKBitmap bitmap, int quality = 90)
    {
        string path = System.IO.Path.Combine(SessionDir, Guid.NewGuid().ToString("N") + ".jpg");
        using var img = SKImage.FromBitmap(bitmap);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
        if (data == null) throw new IOException("Could not store the page image.");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            data.SaveTo(fs);
        return path;
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>Deletes the whole session folder (on exit). Later Save calls create a new one.</summary>
    public static void DeleteSession()
    {
        string? dir;
        lock (Gate)
        {
            dir = _sessionDir;
            _sessionDir = null;
        }
        if (dir == null) return;
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Removes cache folders of sessions whose process is gone (crash or kill).</summary>
    public static void CleanStale()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (string dir in Directory.GetDirectories(Root))
            {
                try
                {
                    string name = System.IO.Path.GetFileName(dir);
                    var parts = name.Split('-');
                    bool alive = false;
                    if (parts.Length >= 3 && parts[0] == "s" && int.TryParse(parts[1], out int pid))
                    {
                        if (pid == Environment.ProcessId) alive = true;
                        else
                        {
                            try
                            {
                                using var p = Process.GetProcessById(pid);
                                alive = !p.HasExited;
                            }
                            catch { alive = false; }
                        }
                    }

                    if (!alive) Directory.Delete(dir, recursive: true);
                }
                catch { /* skip this folder */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Cache cleanup", ex);
        }
    }
}
