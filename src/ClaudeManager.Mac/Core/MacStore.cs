using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeManager.Core;

namespace ClaudeManager.Mac.Core;

/// <summary>Local log file only (~/Library/Logs/ClaudeManager). Never logs tokens or file contents.</summary>
public static class MacLog
{
    static readonly object Gate = new();
    public static string FilePath => Path.Combine(MacPaths.LogDir, "claude-manager.log");
    public static void Info(string m) => Write("INFO", m);
    public static void Error(string m) => Write("ERROR", m);

    static void Write(string level, string message)
    {
        if (!OperatingSystem.IsMacOS()) return; // demo/preview runs on other systems never write
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(MacPaths.LogDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

/// <summary>profiles.json with a .bak copy; a damaged file is restored from the backup automatically.</summary>
public static class MacStore
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    static bool mainIsBad;
    public static string? LoadNote { get; private set; }

    /// <summary>Saving is disabled in demo mode and on non-macOS systems (preview only).</summary>
    public static bool ReadOnly => MacDemo.On || !OperatingSystem.IsMacOS();

    public static AppState Load(string? file = null)
    {
        file ??= MacPaths.ProfilesFile;
        var bak = file + ".bak";
        if (!File.Exists(file))
            return File.Exists(bak) && TryRead(bak, out var orphan) ? orphan! : new AppState();
        if (TryRead(file, out var s)) return s!;
        mainIsBad = true;
        try { File.Copy(file, file + $".bad-{DateTime.Now:yyyyMMddHHmmss}"); } catch { }
        if (File.Exists(bak) && TryRead(bak, out var restored))
        {
            LoadNote = "Your profile list was damaged, so it was restored from the last backup.";
            MacLog.Error("profiles.json damaged; restored from .bak");
            return restored!;
        }
        LoadNote = "Your profile list was damaged and no backup was usable, so Claude Manager started fresh. Your profile folders were not touched.";
        return new AppState();
    }

    static bool TryRead(string path, out AppState? state)
    {
        state = null;
        try
        {
            state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), Options);
            if (state == null || state.Profiles.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id))) return false;
            return true;
        }
        catch { return false; }
    }

    public static void Save(AppState state, string? file = null)
    {
        if (ReadOnly && file == null) return;
        file ??= MacPaths.ProfilesFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        MacSafety.AssertWritable(file);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
        if (File.Exists(file) && !mainIsBad) File.Copy(file, file + ".bak", true);
        File.Move(tmp, file, true);
        mainIsBad = false;
    }
}
