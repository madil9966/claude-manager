using System.Text;

namespace ClaudeManager.Mac.Core;

/// <summary>macOS locations. The user's existing Claude data is listed so it can be protected, never written.</summary>
public static class MacPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string AppSupport => Path.Combine(Home, "Library", "Application Support");

    // Claude Manager's own files.
    public static string AppDir => Path.Combine(AppSupport, "ClaudeManager");
    public static string ProfilesFile => Path.Combine(AppDir, "profiles.json");
    public static string LaunchersDir => Path.Combine(AppDir, "launchers");
    public static string LogDir => Path.Combine(Home, "Library", "Logs", "ClaudeManager");

    // Existing Claude setup (never written to).
    public static string HomeClaudeDir => Path.Combine(Home, ".claude");
    public static string HomeClaudeJson => Path.Combine(Home, ".claude.json");
    public static string DefaultDesktopDir => Path.Combine(AppSupport, "Claude");

    /// <summary>CLAUDE_CONFIG_DIR if set in this app's environment (apps started from Finder usually don't inherit shell variables).</summary>
    public static string? GlobalClaudeConfigDir
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(v)) return null;
            try { return Normalize(v); } catch { return null; }
        }
    }

    public static string ExistingCliDir => GlobalClaudeConfigDir ?? HomeClaudeDir;

    public static string Normalize(string path)
    {
        var p = path.Trim().Trim('"', '\'');
        if (p == "~" || p.StartsWith("~/")) p = Home + p[1..];
        p = Path.GetFullPath(p);
        if (p.Length > 1) p = p.TrimEnd('/', '\\');
        return p;
    }

    public static string Shorten(string path) =>
        path.StartsWith(Home, StringComparison.Ordinal) ? "~" + path[Home.Length..] : path;

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
}
