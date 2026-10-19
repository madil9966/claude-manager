using System.Diagnostics;
using System.Text;

namespace ClaudeManager.Core;

/// <summary>
/// Opens Windows Terminal (cmd.exe) for a profile. CLAUDE_CONFIG_DIR is set inside a small
/// per-profile .cmd script, so it only applies to that terminal and is never set machine-wide.
/// </summary>
public static class Terminal
{
    public static string? FindWindowsTerminal()
    {
        var p = Path.Combine(Paths.LocalAppData, "Microsoft", "WindowsApps", "wt.exe");
        return File.Exists(p) ? p : Paths.FindOnPath("wt", ".exe");
    }

    public static string BuildScript(Profile p, bool clearKeyVars, string command)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("title " + Esc(p.TerminalTitle));
        if (p.IsDefault)
        {
            // Existing setup: keep whatever the user's normal setup is.
            var global = Paths.GlobalClaudeConfigDir;
            sb.AppendLine(global == null ? "set \"CLAUDE_CONFIG_DIR=\"" : $"set \"CLAUDE_CONFIG_DIR={global.Replace("%", "%%")}\"");
        }
        else
        {
            if (string.IsNullOrEmpty(p.CliConfigDir)) throw new InvalidOperationException("Profile has no Claude Code folder.");
            sb.AppendLine($"set \"CLAUDE_CONFIG_DIR={p.CliConfigDir.Replace("%", "%%")}\"");
        }
        if (clearKeyVars)
            foreach (var v in ClaudeCli.OverrideVars) sb.AppendLine($"set \"{v}=\"");
        sb.AppendLine("echo.");
        sb.AppendLine("echo   Claude Code profile: " + Esc(p.Name));
        sb.AppendLine(p.IsDefault ? "echo   Config folder: your existing setup" : "set CLAUDE_CONFIG_DIR");
        sb.AppendLine("echo   Type /status inside Claude to confirm which account is active.");
        sb.AppendLine("echo.");
        sb.AppendLine(command);
        return sb.ToString();
    }

    public static string WriteScript(Profile p, bool clearKeyVars, string command, string suffix)
    {
        Directory.CreateDirectory(Paths.LaunchersDir);
        var path = Path.Combine(Paths.LaunchersDir, p.Id + suffix + ".cmd");
        SafetyGuard.AssertWritable(path);
        File.WriteAllText(path, BuildScript(p, clearKeyVars, command), new UTF8Encoding(false));
        return path;
    }

    public static string Open(Profile p, string workDir, bool clearKeyVars, string command = "claude", string suffix = "")
    {
        if (!p.IsDefault && SafetyGuard.CheckProfileFolder(p.CliConfigDir ?? "") is { } err)
            throw new InvalidOperationException($"The profile's Claude Code folder {err}");

        var script = WriteScript(p, clearKeyVars, command, suffix);
        var dir = Directory.Exists(workDir) ? workDir : Paths.UserProfile;
        var wt = FindWindowsTerminal();
        if (wt != null)
        {
            try
            {
                var args = $"new-tab --title {Q(p.TerminalTitle)} --tabColor {p.Color} --suppressApplicationTitle " +
                           $"-d {Q(dir.EndsWith('\\') ? dir + "." : dir)} cmd.exe /k call {Q(script)}";
                // No redirected pipes and no handles kept: the terminal is fully independent of Claude Manager.
                Process.Start(new ProcessStartInfo(wt, args) { UseShellExecute = false, WorkingDirectory = dir })?.Dispose();
                return $"wt dir={dir} script={script}";
            }
            catch (Exception ex) { Log.Error($"Windows Terminal failed, using cmd.exe: {ex.Message}"); }
        }
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k call \"{script}\"") { UseShellExecute = true, WorkingDirectory = dir })?.Dispose();
        return $"cmd dir={dir} script={script}";
    }

    static string Q(string s) => "\"" + s.Replace(";", "\\;") + "\"";

    /// <summary>Escapes text for title/echo lines in a batch file.</summary>
    public static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if ("&|<>^()".IndexOf(c) >= 0) sb.Append('^');
            if (c == '%') sb.Append('%');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
