using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeManager.Core;

/// <summary>Reads and writes %APPDATA%\ClaudeManager\profiles.json. No credentials are stored.</summary>
public static class ProfileStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static string BackupFile => Paths.ProfilesFile + ".bak";

    /// <summary>True when profiles.json was unreadable at load: the next save must not copy it over the good .bak.</summary>
    static bool mainIsBad;

    /// <summary>Set when profiles had to be recovered (or reset); shown to the user once at startup.</summary>
    public static string? LoadNote { get; private set; }

    public static AppState Load()
    {
        if (!File.Exists(Paths.ProfilesFile))
            return File.Exists(BackupFile) && TryRead(BackupFile, out var orphan, out _) ? Recovered(orphan!, "profiles.json was missing") : new AppState();

        if (TryRead(Paths.ProfilesFile, out var state, out var error)) return state!;

        Log.Error($"profiles.json could not be read: {error}");
        mainIsBad = true;
        var badCopy = Paths.ProfilesFile + $".bad-{DateTime.Now:yyyyMMddHHmmss}";
        try { File.Copy(Paths.ProfilesFile, badCopy); } catch { }

        if (File.Exists(BackupFile) && TryRead(BackupFile, out var backup, out var bakError))
            return Recovered(backup!, $"profiles.json was damaged ({error})");

        Log.Error("profiles.json.bak is missing or unreadable too; starting with no profiles");
        LoadNote = $"Your profile list (profiles.json) was damaged and no usable backup was found, so Claude Manager started fresh.\n\n" +
                   $"A copy of the damaged file was kept at {badCopy}. Your profile folders were not touched; " +
                   "add them again with Add profile (existing folders are reused as they are).";
        return new AppState { FirstRunDone = false };
    }

    static AppState Recovered(AppState state, string why)
    {
        Log.Info($"{why}; restored {state.Profiles.Count} profiles from profiles.json.bak");
        LoadNote = $"{why}, so your profiles were restored from the last backup (profiles.json.bak). " +
                   "Changes made just before the problem may be missing.";
        return state;
    }

    static bool TryRead(string path, out AppState? state, out string? error)
    {
        state = null;
        error = null;
        try
        {
            state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), Options);
            if (state == null) { error = "file is empty"; return false; }
            state.Profiles ??= new();
            if (state.Profiles.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id))) { error = "a profile entry is incomplete"; return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public static void Save(AppState state)
    {
        Directory.CreateDirectory(Paths.AppDir);
        SafetyGuard.AssertWritable(Paths.ProfilesFile);
        var tmp = Paths.ProfilesFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
        // Keep the previous good file as .bak (never a damaged one).
        if (File.Exists(Paths.ProfilesFile) && !mainIsBad)
            File.Copy(Paths.ProfilesFile, BackupFile, overwrite: true);
        File.Move(tmp, Paths.ProfilesFile, overwrite: true);
        mainIsBad = false;
    }
}
