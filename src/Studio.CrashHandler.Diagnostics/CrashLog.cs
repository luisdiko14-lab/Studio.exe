using System.Text;

namespace Studio.CrashHandler.Diagnostics;

public static class CrashLog
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Luis Apps Inc.", "Studio.exe", "Logs");

    public static string LogPath => Path.Combine(LogDirectory, "crash-handler.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var content = $"[{DateTimeOffset.Now:O}] {message}";
            if (exception is not null)
                content += Environment.NewLine + exception;
            File.AppendAllText(LogPath, content + Environment.NewLine + new string('-', 72) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
            // Crash reporting must never cause another crash.
        }
    }
}
