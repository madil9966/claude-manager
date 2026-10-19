using ClaudeManager.Core;

namespace ClaudeManager.Mac.Core;

/// <summary>
/// Single gate for every write and delete on macOS. ~/.claude, ~/.claude.json, ~/Library/Application Support/Claude
/// (and a CLAUDE_CONFIG_DIR) can never be written to, used as a profile folder, or deleted. Symlinks anywhere in a
/// path are resolved first. Comparisons ignore case, like the default APFS volume.
/// </summary>
public static class MacSafety
{
    const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    public static IReadOnlyList<string> ProtectedPaths
    {
        get
        {
            var list = new List<string> { MacPaths.HomeClaudeDir, MacPaths.HomeClaudeJson, MacPaths.DefaultDesktopDir };
            if (MacPaths.GlobalClaudeConfigDir is { } g) list.Add(g);
            return list;
        }
    }

    static IEnumerable<string> BroadFolders => new[]
    {
        MacPaths.Home, MacPaths.AppSupport, Path.Combine(MacPaths.Home, "Library"),
        Path.Combine(MacPaths.Home, "Documents"), Path.Combine(MacPaths.Home, "Desktop"), Path.Combine(MacPaths.Home, "Downloads"),
        "/Applications", "/System", "/Library", "/Users", "/usr", "/bin", "/etc", "/private", "/var", "/tmp", "/Volumes", "/opt",
    };

    public static bool IsSameOrInside(string path, string root)
    {
        path = MacPaths.Normalize(path);
        root = MacPaths.Normalize(root);
        if (root == "/") return true;
        var sep = Path.DirectorySeparatorChar; // '/' on macOS (also correct when previewing on Windows)
        return path.Equals(root, OIC) || path.StartsWith(root.TrimEnd('/', '\\') + sep, OIC);
    }

    /// <summary>Where a path really leads: every existing component's symlink is resolved; the missing rest is appended.</summary>
    public static string RealPath(string path)
    {
        var full = MacPaths.Normalize(path);
        try
        {
            var root = Path.GetPathRoot(full) ?? "/";
            var current = root;
            var parts = full[root.Length..].Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var next = Path.Combine(current, parts[i]);
                FileSystemInfo? info = Directory.Exists(next) ? new DirectoryInfo(next) : File.Exists(next) ? new FileInfo(next) : null;
                if (info == null)
                {
                    // Doesn't exist yet: nothing more to resolve.
                    return MacPaths.Normalize(Path.Combine(new[] { current }.Concat(parts[i..]).ToArray()));
                }
                current = info.LinkTarget != null ? info.ResolveLinkTarget(true)?.FullName ?? next : next;
            }
            return MacPaths.Normalize(current);
        }
        catch { return full; }
    }

    static IEnumerable<string> Forms(string path)
    {
        var n = MacPaths.Normalize(path);
        yield return n;
        var real = RealPath(n);
        if (!real.Equals(n, OIC)) yield return real;
    }

    static IEnumerable<string> ProtectedForms => ProtectedPaths.SelectMany(Forms).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the folder may be used as a profile folder, else the reason (completes "Folder X ...").</summary>
    public static string? CheckProfileFolder(string path)
    {
        string n;
        try
        {
            var raw = path.Trim();
            if (!raw.StartsWith('/') && !raw.StartsWith('~') && !Path.IsPathFullyQualified(raw))
                return "must be a full path, like ~/.claude-work.";
            n = MacPaths.Normalize(raw);
        }
        catch { return "is not a valid folder path."; }
        if (n == "/" || Path.GetPathRoot(n) == n) return "is the root of a disk.";

        foreach (var c in Forms(n))
        {
            foreach (var root in ProtectedForms)
            {
                if (IsSameOrInside(c, root)) return $"is inside your existing Claude data ({MacPaths.Shorten(root)}), which Claude Manager never touches.";
                if (IsSameOrInside(root, c)) return $"contains your existing Claude data ({MacPaths.Shorten(root)}). Pick a dedicated folder.";
            }
            foreach (var app in Forms(MacPaths.AppDir))
                if (IsSameOrInside(c, app) || IsSameOrInside(app, c)) return "overlaps Claude Manager's own settings folder.";
            foreach (var broad in BroadFolders)
                if (c.Equals(MacPaths.Normalize(broad), OIC)) return "is a system or user folder. Pick a dedicated subfolder.";
        }
        return null;
    }

    public static string? CheckDeletable(string path)
    {
        if (CheckProfileFolder(path) is { } e) return e;
        var n = MacPaths.Normalize(path);
        try { if (new DirectoryInfo(n).LinkTarget != null) return "is a link to another folder, so it is kept."; } catch { }
        foreach (var form in Forms(n))
        foreach (var broad in BroadFolders)
            if (IsSameOrInside(MacPaths.Normalize(broad), form)) return "contains important folders and is too broad to delete.";
        return null;
    }

    public static void AssertWritable(string path)
    {
        foreach (var form in Forms(path))
        foreach (var root in ProtectedForms)
            if (IsSameOrInside(form, root))
            {
                MacLog.Error($"BLOCKED write to protected path {path} (real location {form})");
                throw new UnauthorizedAccessException($"Claude Manager refused to write to {path}: it belongs to your existing Claude setup.");
            }
    }

    /// <summary>Another profile whose folder is the same as, inside, or contains this path.</summary>
    public static Profile? OverlappingProfile(AppState state, string path, Profile? except)
    {
        var mine = Forms(path).ToList();
        foreach (var p in state.Profiles)
        {
            if (ReferenceEquals(p, except) || p.IsDefault) continue;
            foreach (var dir in new[] { p.DesktopDataDir, p.CliConfigDir })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                foreach (var a in mine)
                foreach (var b in Forms(dir))
                    if (IsSameOrInside(a, b) || IsSameOrInside(b, a)) return p;
            }
        }
        return null;
    }
}
