namespace LivelyPlaylist;

/// <summary>Minimal file + trace logger. Log file sits next to the executable.</summary>
public static class Logger
{
    private static readonly object _lock = new();

    public static string LogPath =>
        Path.Combine(AppContext.BaseDirectory, "lively-playlist.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    public static void Error(string msg, Exception ex) => Write("ERROR", $"{msg}: {ex.Message}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (_lock)
        {
            try
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // Never let logging crash the app.
            }
        }
    }
}
