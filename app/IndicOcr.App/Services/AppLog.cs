using System.Text;

namespace IndicOcr.App;

/// <summary>Tiny local file log. Writes exception types and stack traces only; never document text.</summary>
internal static class AppLog
{
    public static void Error(string context, Exception? ex)
    {
        try
        {
            string dir = Services.AppPaths.DataDirectory;
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("s")).Append(' ').Append(context);
            if (ex != null)
            {
                sb.Append(" | ").Append(ex.GetType().FullName).AppendLine();
                sb.Append(ex.StackTrace);
            }
            sb.AppendLine();
            File.AppendAllText(System.IO.Path.Combine(dir, "app.log"), sb.ToString());
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
