using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeManager.Core;

/// <summary>
/// Single gate for every write and delete Claude Manager performs.
/// The user's existing Claude data (~\.claude, ~\.claude.json, %APPDATA%\Claude and a
/// globally set CLAUDE_CONFIG_DIR) can never be written to, used as a profile folder, or deleted.
/// </summary>
public static class SafetyGuard
{
    const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    public static IReadOnlyList<string> ProtectedPaths
    {
        get
        {
            var list = new List<string> { Paths.HomeClaudeDir, Paths.HomeClaudeJson, Paths.DefaultDesktopDir };
            var g = Paths.GlobalClaudeConfigDir;
            if (g != null) list.Add(g);
            return list;
        }
    }

    static IEnumerable<string> BroadFolders => new[]
    {
        Paths.UserProfile, Paths.RoamingAppData, Paths.LocalAppData, Paths.AccountsParent,
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),          // Start menu
        Path.Combine(Paths.LocalAppData, "Programs"),                          // per-user installs
        Path.Combine(Paths.LocalAppData, "Temp"),
        Path.GetTempPath(),
    }.Where(s => !string.IsNullOrEmpty(s));

    public static bool IsSameOrInside(string path, string root)
    {
        path = Paths.Normalize(path);
        root = Paths.Normalize(root);
        return path.Equals(root, OIC) || path.StartsWith(root.TrimEnd('\\') + "\\", OIC);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr sa, FileMode mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetFinalPathNameByHandle(SafeFileHandle h, StringBuilder buf, int size, int flags);
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    /// <summary>
    /// The real location of a path: the deepest existing part is resolved by Windows (junctions and
    /// symlinks anywhere in the path, 8.3 short names, subst drives), and the not-yet-existing rest is appended.
    /// Returns the normalized input if nothing can be resolved.
    /// </summary>
    public static string RealPath(string path)
    {
        var full = Paths.Normalize(path);
        var existing = full;
        var rest = new Stack<string>();
        while (existing != null && !Directory.Exists(existing) && !File.Exists(existing))
        {
            rest.Push(Path.GetFileName(existing));
            existing = Path.GetDirectoryName(existing);
        }
        if (existing == null) return full;
        var resolved = Resolve(existing) ?? existing;
        while (rest.Count > 0) resolved = Path.Combine(resolved, rest.Pop());
        try { return Paths.Normalize(resolved); } catch { return full; }
    }

    static string? Resolve(string existing)
    {
        try
        {
            using var h = CreateFile(existing, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var sb = new StringBuilder(1024);
            int n = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0);
            if (n <= 0 || n >= sb.Capacity) return null;
            var s = sb.ToString();
            if (s.StartsWith(@"\\?\UNC\", OIC)) return @"\\" + s[8..];
            if (s.StartsWith(@"\\?\", OIC)) return s[4..];
            return s;
        }
        catch { return null; }
    }

    /// <summary>Both the written form and the real location of a path (deduplicated).</summary>
    static IEnumerable<string> Forms(string path)
    {
        var n = Paths.Normalize(path);
        yield return n;
        var real = RealPath(n);
        if (!real.Equals(n, OIC)) yield return real;
    }

    static IEnumerable<string> ProtectedForms => ProtectedPaths.SelectMany(Forms).Distinct(StringComparer.OrdinalIgnoreCase);

    static string? ResolveLink(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return di.ResolveLinkTarget(true)?.FullName;
        }
        catch { }
        return null;
    }

    /// <summary>Returns null when the folder may be used as a profile folder, else the reason (completes "Folder X ...").</summary>
    public static string? CheckProfileFolder(string path)
    {
        string n;
        try
        {
            var raw = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (raw.StartsWith(@"\\") || raw.StartsWith("//"))
                return "is a network or device path. Use a folder on a local drive, like C:\\Users\\you\\.claude-work.";
            if (!raw.StartsWith('~') && !Path.IsPathFullyQualified(raw))
                return "must be a full path, like C:\\Users\\you\\.claude-work.";
            n = Paths.Normalize(path);
        }
        catch { return "is not a valid folder path."; }
        if (n.Length <= 3) return "is a drive root.";

        // Check where the path really leads too (links anywhere in it, short names, subst drives).
        var candidates = new List<string> { n };
        var real = RealPath(n);
        if (!real.Equals(n, OIC)) candidates.Add(real);
        if (ResolveLink(n) is { } target) candidates.Add(target);
        if (candidates.Any(c => c.StartsWith(@"\\"))) return "leads to a network location. Use a folder on a local drive.";

        foreach (var c in candidates)
        {
            foreach (var root in ProtectedForms)
            {
                if (IsSameOrInside(c, root)) return $"is inside your existing Claude data ({root}), which Claude Manager never touches.";
                if (IsSameOrInside(root, c)) return $"contains your existing Claude data ({root}). Pick a dedicated folder.";
            }
            foreach (var app in Forms(Paths.AppDir))
                if (IsSameOrInside(c, app) || IsSameOrInside(app, c)) return "overlaps Claude Manager's own settings folder.";
            foreach (var broad in BroadFolders.SelectMany(Forms))
                if (c.Equals(broad, OIC)) return "is a system or user folder. Pick a dedicated subfolder.";
        }
        return null;
    }

    /// <summary>Returns null when the folder may be deleted recursively, else the reason.</summary>
    public static string? CheckDeletable(string path)
    {
        var e = CheckProfileFolder(path);
        if (e != null) return e;
        var n = Paths.Normalize(path);
        if (ResolveLink(n) != null) return "is a link to another folder, so it is kept.";
        foreach (var form in Forms(n))
        foreach (var broad in BroadFolders.SelectMany(Forms))
            if (IsSameOrInside(broad, form)) return "contains important folders and is too broad to delete.";
        return null;
    }

    /// <summary>Throws if the path (or where it really leads) belongs to the user's existing Claude setup.</summary>
    public static void AssertWritable(string path)
    {
        var n = Paths.Normalize(path);
        foreach (var form in Forms(n))
        foreach (var root in ProtectedForms)
        {
            if (IsSameOrInside(form, root))
            {
                Log.Error($"BLOCKED write to protected path {n} (real location {form})");
                throw new UnauthorizedAccessException(
                    $"Claude Manager refused to write to {n}: it belongs to your existing Claude setup.");
            }
        }
    }
}
