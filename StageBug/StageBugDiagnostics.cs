namespace StageBug;

public static class StageBugDiagnostics
{
    private static readonly object Sync = new();
    private static readonly string LogPath = CreateLogPath();

    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Diagnostics must never interfere with the application.
        }
    }

    private static string CreateLogPath()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StageBug");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "stagebug.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "stagebug.log");
        }
    }
}
