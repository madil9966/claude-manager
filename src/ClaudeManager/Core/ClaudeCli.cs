using System.Text.RegularExpressions;

namespace ClaudeManager.Core;

public sealed record CliInstall(bool Found, string? Path, Version? Version, string? Raw)
{
    public bool TooOld => Version != null && Version < ClaudeCli.MinVersion;

    /// <summary>Installed, but the version couldn't be read (timeout, odd output). Treated as unsafe for profiles.</summary>
    public bool Unverified => Found && Version == null;

    public string Describe() =>
        !Found ? "Claude Code: not installed"
        : Version == null ? "Claude Code: found, but its version could not be checked"
        : TooOld ? $"Claude Code {Version}: older than {ClaudeCli.MinVersion}"
        : $"Claude Code {Version}";
}

public static class ClaudeCli
{
    /// <summary>Older builds read/write %USERPROFILE%\.claude.json regardless of CLAUDE_CONFIG_DIR.</summary>
    public static readonly Version MinVersion = new(2, 1, 140);
    public const string InstallUrl = "https://docs.claude.com/en/docs/claude-code/setup";
    public static readonly string[] OverrideVars = { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN" };

    public static CliInstall Detect()
    {
        try
        {
            var path = Paths.FindOnPath("claude", ".exe", ".cmd", ".bat");
            if (path == null) { Log.Info("Claude Code not found on PATH"); return new(false, null, null, null); }
            var (_, output) = Proc.Run("cmd.exe", $"/d /c \"\"{path}\" --version\"", 20000);
            var m = Regex.Match(output, @"(\d+)\.(\d+)\.(\d+)");
            Version? v = m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
            Log.Info($"Claude Code detected: {path} version={v?.ToString() ?? "unknown"}");
            return new(true, path, v, output.Trim());
        }
        catch (Exception ex)
        {
            Log.Error($"Claude Code detection failed: {ex.Message}");
            return new(false, null, null, null);
        }
    }

    /// <summary>Names (and scope) of set variables that override a profile's stored login. Values are never read beyond "is set".</summary>
    public static List<string> OverridingVars()
    {
        var list = new List<string>();
        foreach (var v in OverrideVars)
        {
            var scopes = new List<string>();
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v, EnvironmentVariableTarget.User))) scopes.Add("user");
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v, EnvironmentVariableTarget.Machine))) scopes.Add("system");
            if (scopes.Count == 0 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))) scopes.Add("inherited");
            if (scopes.Count > 0) list.Add($"{v} ({string.Join(", ", scopes)} environment)");
        }
        return list;
    }
}
