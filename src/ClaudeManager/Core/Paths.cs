using System.Text;

namespace ClaudeManager.Core;

/// <summary>Well-known locations and path helpers.</summary>
public static class Paths
{
    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Claude Manager's own files.
    public static string AppDir => Path.Combine(RoamingAppData, "ClaudeManager");
    public static string ProfilesFile => Path.Combine(AppDir, "profiles.json");
    public static string LogDir => Path.Combine(AppDir, "logs");
    public static string LaunchersDir => Path.Combine(AppDir, "launchers");
    public static string IconsDir => Path.Combine(AppDir, "icons");
    public static string BackupDir => Path.Combine(UserProfile, "ClaudeManager-Backups");

    // The user's existing Claude setup (never written to).
    public static string HomeClaudeDir => Path.Combine(UserProfile, ".claude");
    public static string HomeClaudeJson => Path.Combine(UserProfile, ".claude.json");
    public static string DefaultDesktopDir => Path.Combine(RoamingAppData, "Claude");

    /// <summary>CLAUDE_CONFIG_DIR if the user set it globally (User or Machine scope), else null.</summary>
    public static string? GlobalClaudeConfigDir
    {
        get
        {
            foreach (var t in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                var v = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR", t);
                if (!string.IsNullOrWhiteSpace(v))
                {
                    try { return Normalize(v); } catch { }
                }
            }
            return null;
        }
    }

    /// <summary>The Claude Code folder the user's existing (default) setup uses.</summary>
    public static string ExistingCliDir => GlobalClaudeConfigDir ?? HomeClaudeDir;

    public static string AccountsParent => Path.Combine(UserProfile, ".claude-accounts");

    public static string Normalize(string path)
    {
        var p = path.Trim().Trim('"');
        if (p == "~" || p.StartsWith("~\\") || p.StartsWith("~/")) p = UserProfile + p[1..];
        p = Path.GetFullPath(Environment.ExpandEnvironmentVariables(p));
        if (p.Length > 3) p = p.TrimEnd('\\', '/');
        return p;
    }

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "profile" : s;
    }

    public static string Shorten(string path)
    {
        var home = UserProfile;
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    public static string? FindOnPath(string name, params string[] extensions)
    {
        var dirs = new List<string>();
        foreach (var t in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var v = Environment.GetEnvironmentVariable("PATH", t);
            if (!string.IsNullOrEmpty(v))
                dirs.AddRange(v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var ext in extensions)
        {
            try
            {
                var p = Path.Combine(Environment.ExpandEnvironmentVariables(dir), name + ext);
                if (File.Exists(p)) return p;
            }
            catch { /* bad PATH entry */ }
        }
        return null;
    }
}
