using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeManager.Core;

namespace ClaudeManager.Mac.Core;

public sealed record MacDesktop(string? AppPath, string? Version)
{
    public bool Found => AppPath != null;
}

public sealed record MacCli(bool Found, string? Path, Version? Version, string? Raw)
{
    public static readonly Version MinVersion = new(2, 1, 140);
    public bool TooOld => Version != null && Version < MinVersion;
    public bool Unverified => Found && Version == null;
}

/// <summary>A running Claude desktop main process, its data folder (null = default) and memory of its process tree.</summary>
public sealed record MacInstance(int Pid, string? UserDataDir, long Bytes);

/// <summary>Finds and launches the Claude desktop app and Claude Code on macOS. Never modifies Claude.</summary>
public static class MacClaude
{
    public const string DownloadUrl = "https://claude.ai/download";
    public const string CliInstallUrl = "https://docs.claude.com/en/docs/claude-code/setup";

    // ---------- desktop app ----------

    public static MacDesktop FindDesktop()
    {
        foreach (var app in new[] { "/Applications/Claude.app", Path.Combine(MacPaths.Home, "Applications", "Claude.app") })
        {
            if (!Directory.Exists(app)) continue;
            string? version = null;
            try
            {
                var plist = File.ReadAllText(Path.Combine(app, "Contents", "Info.plist"));
                var m = Regex.Match(plist, @"<key>CFBundleShortVersionString</key>\s*<string>([^<]+)</string>");
                if (m.Success) version = m.Groups[1].Value;
            }
            catch { }
            return new MacDesktop(app, version);
        }
        return new MacDesktop(null, null);
    }

    /// <summary>`open` arguments: the default profile starts Claude as usual; others get their own data folder.</summary>
    public static List<string> OpenArgs(MacDesktop d, Profile p)
    {
        if (p.IsDefault || string.IsNullOrEmpty(p.DesktopDataDir)) return new() { "-a", d.AppPath! };
        // -n: a separate instance. Claude hands off to an already running instance of the same folder by itself.
        return new() { "-n", "-a", d.AppPath!, "--args", $"--user-data-dir={p.DesktopDataDir}" };
    }

    public static LaunchResult LaunchDesktop(MacDesktop d, Profile p)
    {
        if (!d.Found) return new(false, "The Claude desktop app is not installed.", "not found");
        if (!p.IsDefault && MacSafety.CheckProfileFolder(p.DesktopDataDir ?? "") is { } err)
            return new(false, $"The profile folder {err}", "blocked by safety gate");
        var args = OpenArgs(d, p);
        var (code, output) = Run("/usr/bin/open", args, 15000);
        return code == 0 ? new(true, "", "open " + string.Join(' ', args)) : new(false, output.Trim(), $"open exit {code}");
    }

    // ---------- running instances (ps) ----------

    public static List<MacInstance> FindInstances()
    {
        var list = new List<MacInstance>();
        if (!OperatingSystem.IsMacOS()) return list;
        var (code, output) = Run("/bin/ps", new() { "-axww", "-o", "pid=,ppid=,rss=,command=" }, 5000);
        if (code != 0) return list;
        var procs = new List<(int Pid, int PPid, long Rss, string Cmd)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(line, @"^\s*(\d+)\s+(\d+)\s+(\d+)\s+(.*)$");
            if (m.Success) procs.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), long.Parse(m.Groups[3].Value) * 1024, m.Groups[4].Value));
        }
        var children = procs.ToLookup(p => p.PPid);
        long Tree(int pid)
        {
            long sum = 0;
            var stack = new Stack<int>(new[] { pid });
            var seen = new HashSet<int>();
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!seen.Add(id)) continue;
                sum += procs.Where(p => p.Pid == id).Sum(p => p.Rss);
                foreach (var c in children[id]) stack.Push(c.Pid);
            }
            return sum;
        }
        foreach (var p in procs)
        {
            if (!p.Cmd.Contains("/Claude.app/Contents/MacOS/Claude", StringComparison.Ordinal) || p.Cmd.Contains("--type=")) continue;
            list.Add(new MacInstance(p.Pid, ParseUserDataDir(p.Cmd), Tree(p.Pid)));
        }
        return list;
    }

    public static string? ParseUserDataDir(string cmd)
    {
        const string flag = "--user-data-dir=";
        var i = cmd.IndexOf(flag, StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = cmd[(i + flag.Length)..];
        var end = rest.IndexOf(" --", StringComparison.Ordinal); // paths may contain spaces; the next flag ends it
        var value = (end < 0 ? rest : rest[..end]).Trim().Trim('"');
        try { return MacPaths.Normalize(value); } catch { return value; }
    }

    public static MacInstance? InstanceFor(Profile p, IEnumerable<MacInstance> instances)
    {
        if (p.IsDefault) return instances.FirstOrDefault(i => i.UserDataDir == null || i.UserDataDir.Equals(MacPaths.DefaultDesktopDir, StringComparison.OrdinalIgnoreCase));
        if (p.DesktopDataDir == null) return null;
        var want = MacPaths.Normalize(p.DesktopDataDir);
        return instances.FirstOrDefault(i => i.UserDataDir != null && i.UserDataDir.Equals(want, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Claude Code ----------

    public static MacCli FindCli()
    {
        if (!OperatingSystem.IsMacOS()) return new(false, null, null, null);
        // A login shell, so PATH from the user's shell setup (Homebrew, npm, ~/.local/bin) is used.
        var (code, output) = Run("/bin/zsh", new() { "-lic", "command -v claude && claude --version" }, 20000);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var path = lines.FirstOrDefault(l => l.StartsWith('/') && l.EndsWith("claude"));
        if (path == null) return new(false, null, null, output.Trim());
        var m = Regex.Match(output, @"(\d+)\.(\d+)\.(\d+)");
        Version? v = m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
        return new(true, path, v, output.Trim());
    }

    static string Sq(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>The .command script Terminal runs: CLAUDE_CONFIG_DIR is set for that window only.</summary>
    public static string BuildScript(Profile p, string workDir)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/zsh -il\n");
        sb.Append("cd -- ").Append(Sq(workDir)).Append(" || cd ~\n");
        if (!p.IsDefault)
        {
            if (string.IsNullOrEmpty(p.CliConfigDir)) throw new InvalidOperationException("Profile has no Claude Code folder.");
            sb.Append("export CLAUDE_CONFIG_DIR=").Append(Sq(p.CliConfigDir)).Append('\n');
        }
        sb.Append("printf '\\033]0;%s\\007' ").Append(Sq(p.TerminalTitle)).Append('\n');
        sb.Append("clear\n");
        sb.Append("echo\necho ").Append(Sq("  Claude Code profile: " + p.Name)).Append('\n');
        sb.Append(p.IsDefault ? "echo '  Config folder: your existing setup'\n" : "echo \"  Config folder: $CLAUDE_CONFIG_DIR\"\n");
        sb.Append("echo '  Type /status inside Claude to confirm which account is active.'\necho\n");
        sb.Append("claude\n");
        sb.Append("exec \"$SHELL\" -il\n"); // keep the window open (with the profile still set) after Claude exits
        return sb.ToString();
    }

    public static string OpenTerminal(Profile p, string workDir)
    {
        if (!p.IsDefault && MacSafety.CheckProfileFolder(p.CliConfigDir ?? "") is { } err)
            throw new InvalidOperationException($"The profile's Claude Code folder {err}");
        Directory.CreateDirectory(MacPaths.LaunchersDir);
        var script = Path.Combine(MacPaths.LaunchersDir, $"{p.Id}.command");
        MacSafety.AssertWritable(script);
        File.WriteAllText(script, BuildScript(p, Directory.Exists(workDir) ? workDir : MacPaths.Home), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var (code, output) = Run("/usr/bin/open", new() { "-a", "Terminal", script }, 15000);
        if (code != 0) throw new InvalidOperationException($"Terminal could not be opened: {output.Trim()}");
        return $"Terminal script={script} dir={workDir}";
    }

    public static void OpenUrl(string url) { try { Run("/usr/bin/open", new() { url }, 5000); } catch { } }
    public static void Reveal(string path) { try { Run("/usr/bin/open", new() { path }, 5000); } catch { } }

    // ---------- profiles ----------

    public static (string Desktop, string Cli) SuggestFolders(AppState state, string name, Profile? except = null)
    {
        var baseSlug = MacPaths.Slug(name);
        for (int i = 1; ; i++)
        {
            var slug = i == 1 ? baseSlug : $"{baseSlug}-{i}";
            var desktop = Path.Combine(MacPaths.AppSupport, "Claude-" + char.ToUpperInvariant(slug[0]) + slug[1..]);
            var cli = Path.Combine(MacPaths.Home, ".claude-" + slug);
            if (MacSafety.OverlappingProfile(state, desktop, except) == null && MacSafety.OverlappingProfile(state, cli, except) == null
                && MacSafety.CheckProfileFolder(desktop) == null && MacSafety.CheckProfileFolder(cli) == null)
                return (desktop, cli);
        }
    }

    public static void EnsureFolders(Profile p)
    {
        if (p.IsDefault || MacStore.ReadOnly) return;
        foreach (var dir in new[] { p.DesktopDataDir, p.CliConfigDir })
        {
            if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException($"Profile '{p.Name}' has no folder set.");
            if (MacSafety.CheckProfileFolder(dir) is { } err) throw new InvalidOperationException($"Folder {dir} {err}");
            MacSafety.AssertWritable(dir);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                if (!p.CreatedFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)) p.CreatedFolders.Add(dir);
                MacLog.Info($"Created folder {dir} for '{p.Name}'");
            }
        }
    }

    public static bool CanDeleteData(AppState state, Profile p, string? dir) =>
        !p.IsDefault && dir != null && Directory.Exists(dir)
        && p.CreatedFolders.Contains(dir, StringComparer.OrdinalIgnoreCase)
        && MacSafety.CheckDeletable(dir) == null
        && MacSafety.OverlappingProfile(state, dir, p) == null;

    /// <summary>Removes the profile's data folders it created (never links, never shared folders). Returns notes.</summary>
    public static List<string> DeleteData(AppState state, Profile p)
    {
        var notes = new List<string>();
        foreach (var dir in new[] { p.DesktopDataDir, p.CliConfigDir })
        {
            if (dir == null || !Directory.Exists(dir)) continue;
            if (!CanDeleteData(state, p, dir)) { notes.Add($"Kept {MacPaths.Shorten(dir)} (not created by Claude Manager, or shared)."); continue; }
            try { DeleteTree(new DirectoryInfo(dir)); MacLog.Info($"Deleted {dir} of '{p.Name}'"); }
            catch (Exception ex) { notes.Add($"Some files in {MacPaths.Shorten(dir)} could not be removed: {ex.Message}"); }
        }
        return notes;

        static void DeleteTree(DirectoryInfo d)
        {
            foreach (var e in d.EnumerateFileSystemInfos())
            {
                if (e.LinkTarget != null) { e.Delete(); continue; } // remove the link only
                if (e is DirectoryInfo sub) DeleteTree(sub); else e.Delete();
            }
            d.Delete();
        }
    }

    public static bool FolderTouchedSince(string? dir, DateTime sinceUtc)
    {
        try
        {
            var di = new DirectoryInfo(dir ?? "");
            return di.Exists && di.EnumerateFileSystemInfos().Any(e => e.LastWriteTimeUtc >= sinceUtc.AddSeconds(-1));
        }
        catch { return false; }
    }

    // ---------- process helper ----------

    public static (int ExitCode, string Output) Run(string file, List<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, o.Result + e.Result);
    }

    public static string FormatBytes(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : $"{b / (double)(1L << 20):0} MB";
}
