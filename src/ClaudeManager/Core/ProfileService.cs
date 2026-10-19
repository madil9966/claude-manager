namespace ClaudeManager.Core;

public sealed class ProfileInUseException(string message) : Exception(message);

public static class ProfileService
{
    const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    public static (string Desktop, string Cli) SuggestFolders(AppState state, string name, FolderLayout layout, Profile? except = null)
    {
        var baseSlug = Paths.Slug(name);
        for (int i = 1; ; i++)
        {
            var slug = i == 1 ? baseSlug : $"{baseSlug}-{i}";
            string desktop, cli;
            if (layout == FolderLayout.AccountsParent)
            {
                var root = Path.Combine(Paths.AccountsParent, slug);
                desktop = Path.Combine(root, "desktop");
                cli = Path.Combine(root, "cli");
            }
            else
            {
                desktop = Path.Combine(Paths.RoamingAppData, "Claude-" + char.ToUpperInvariant(slug[0]) + slug[1..]);
                cli = Path.Combine(Paths.UserProfile, ".claude-" + slug);
            }
            if (!IsUsedByOther(state, desktop, except) && !IsUsedByOther(state, cli, except)
                && SafetyGuard.CheckProfileFolder(desktop) == null && SafetyGuard.CheckProfileFolder(cli) == null)
                return (desktop, cli);
        }
    }

    public static bool IsUsedByOther(AppState state, string path, Profile? except) => OverlappingProfile(state, path, except) != null;

    /// <summary>
    /// Another profile whose folder is the same as, inside, or contains this path (written form or real
    /// location). Nested profile folders would let deleting one profile wipe another's data.
    /// </summary>
    public static Profile? OverlappingProfile(AppState state, string path, Profile? except)
    {
        string n, real;
        try { n = Paths.Normalize(path); real = SafetyGuard.RealPath(n); } catch { return null; }
        foreach (var p in state.Profiles)
        {
            if (ReferenceEquals(p, except) || p.IsDefault) continue;
            foreach (var dir in new[] { p.DesktopDataDir, p.CliConfigDir })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string o, oReal;
                try { o = Paths.Normalize(dir); oReal = SafetyGuard.RealPath(o); } catch { continue; }
                foreach (var a in new[] { n, real })
                foreach (var b in new[] { o, oReal })
                    if (SafetyGuard.IsSameOrInside(a, b) || SafetyGuard.IsSameOrInside(b, a)) return p;
            }
        }
        return null;
    }

    public static Profile CreateDefault(string name) =>
        new() { Name = name, Color = "#828282", IsDefault = true, DesktopSignedIn = true };

    public static Profile Create(AppState state, string name, string color, string desktopDir, string cliDir)
    {
        var p = new Profile
        {
            Name = name,
            Color = color,
            DesktopDataDir = Paths.Normalize(desktopDir),
            CliConfigDir = Paths.Normalize(cliDir),
        };
        while (state.Profiles.Any(x => x.Id == p.Id)) p.Id = Profile.NewId();
        EnsureFolders(p);
        state.Profiles.Add(p);
        Log.Info($"Created profile '{name}' desktop={p.DesktopDataDir} cli={p.CliConfigDir}");
        return p;
    }

    /// <summary>Creates missing profile folders (re-validated every time) and records which ones the app created.</summary>
    public static void EnsureFolders(Profile p)
    {
        if (p.IsDefault || Demo.On) return; // demo profiles are never created on disk
        foreach (var dir in new[] { p.DesktopDataDir, p.CliConfigDir })
        {
            if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException($"Profile '{p.Name}' has no folder set.");
            var err = SafetyGuard.CheckProfileFolder(dir);
            if (err != null) throw new InvalidOperationException($"Folder {dir} {err}");
            SafetyGuard.AssertWritable(dir);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                if (!p.CreatedFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)) p.CreatedFolders.Add(dir);
                Log.Info($"Created folder {dir} for '{p.Name}'");
            }
        }
    }

    public static bool CanDeleteData(AppState state, Profile p, string? dir)
    {
        if (p.IsDefault || dir == null || !Directory.Exists(dir)) return false;
        if (!p.CreatedFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)) return false;
        if (SafetyGuard.CheckDeletable(dir) != null) return false;
        return !IsUsedByOther(state, dir, p);
    }

    public static List<string> Delete(AppState state, Profile p, bool deleteData)
    {
        var notes = new List<string>();
        if (p.IsDefault) { notes.Add("Your existing setup cannot be deleted."); return notes; }

        if (deleteData)
        {
            // All-or-nothing: first move every folder aside. Windows refuses to rename a folder while any
            // file inside is open, so if Claude or a terminal still uses it nothing is deleted at all.
            var moved = new List<(string Original, string Trash)>();
            foreach (var dir in new[] { p.DesktopDataDir, p.CliConfigDir })
            {
                if (dir == null || !Directory.Exists(dir)) continue;
                if (!CanDeleteData(state, p, dir))
                {
                    var other = OverlappingProfile(state, dir, p);
                    notes.Add(other != null
                        ? $"Kept {dir}: it overlaps a folder of profile \"{other.Name}\"."
                        : $"Kept {dir} (not created by Claude Manager).");
                    continue;
                }
                var trash = $"{dir}.deleting-{DateTime.Now:yyyyMMddHHmmss}";
                try
                {
                    SafetyGuard.AssertWritable(trash);
                    Directory.Move(dir, trash);
                    moved.Add((dir, trash));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    foreach (var (orig, t) in moved) { try { Directory.Move(t, orig); } catch { } }
                    Log.Error($"Delete of profile '{p.Name}' cancelled, {dir} is in use: {ex.Message}");
                    throw new ProfileInUseException(
                        $"Nothing was deleted: {dir} is in use. Quit {p.DesktopTitle}, close its {p.TerminalTitle} terminals, then try again.");
                }
            }
            foreach (var (orig, trash) in moved)
            {
                try
                {
                    DeleteTree(trash);
                    Log.Info($"Deleted folder {orig} of profile '{p.Name}'");
                    RemoveEmptyParents(orig);
                }
                catch (Exception ex)
                {
                    notes.Add($"Removed from the profile, but some leftover files remain in {trash}: {ex.Message} You can delete that folder by hand.");
                    Log.Error($"Delete {trash} failed: {ex.Message}");
                }
            }
        }

        // The caller removes p from state.Profiles on the UI thread.
        foreach (var f in Directory.Exists(Paths.LaunchersDir) ? Directory.GetFiles(Paths.LaunchersDir, p.Id + "*.cmd") : Array.Empty<string>())
            try { File.Delete(f); } catch { }
        Log.Info($"Deleted profile '{p.Name}' (data {(deleteData ? "removed" : "kept")})");
        return notes;
    }

    /// <summary>Recursive delete that never follows junctions or symlinks (only the link itself is removed).</summary>
    static void DeleteTree(string dir)
    {
        SafetyGuard.AssertWritable(dir);
        if (SafetyGuard.CheckDeletable(dir) is { } reason) throw new InvalidOperationException($"{dir} {reason}");
        Walk(new DirectoryInfo(dir));

        static void Walk(DirectoryInfo info)
        {
            foreach (var e in info.EnumerateFileSystemInfos())
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) { e.Delete(); continue; }
                if (e is DirectoryInfo sub) Walk(sub);
                else { e.Attributes = FileAttributes.Normal; e.Delete(); }
            }
            info.Attributes = FileAttributes.Directory;
            info.Delete();
        }
    }

    static void RemoveEmptyParents(string dir)
    {
        var parent = Path.GetDirectoryName(dir);
        while (parent != null && SafetyGuard.CheckDeletable(parent) == null
               && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            try { Directory.Delete(parent); } catch { break; }
            parent = Path.GetDirectoryName(parent);
        }
    }

    /// <summary>True when something inside the folder was created or changed after the given time.</summary>
    public static bool FolderTouchedSince(string? dir, DateTime sinceUtc)
    {
        if (dir == null) return false;
        try
        {
            var di = new DirectoryInfo(dir);
            if (!di.Exists) return false;
            var t = sinceUtc.AddSeconds(-1);
            return di.EnumerateFileSystemInfos().Any(e => e.LastWriteTimeUtc >= t || e.CreationTimeUtc >= t);
        }
        catch { return false; }
    }

    public static bool IsEmptyDir(string? dir)
    {
        try { return dir == null || !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }
}
