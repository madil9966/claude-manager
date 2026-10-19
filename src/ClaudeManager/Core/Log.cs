namespace ClaudeManager.Core;

/// <summary>Local log file only. Never logs tokens or file contents.</summary>
public static class Log
{
    static readonly object Gate = new();
    public static string FilePath => Path.Combine(Paths.LogDir, "claude-manager.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Launch(string profile, string kind, bool ok, string detail) =>
        Write(ok ? "LAUNCH-OK" : "LAUNCH-FAIL", $"{kind} profile='{profile}' {detail}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.LogDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
