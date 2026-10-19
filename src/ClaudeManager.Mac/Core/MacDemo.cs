using ClaudeManager.Core;

namespace ClaudeManager.Mac.Core;

/// <summary>CLAUDE_MANAGER_DEMO=1 (or any non-macOS system): sample profiles for screenshots/preview; nothing is saved.</summary>
public static class MacDemo
{
    public static bool On { get; } = Environment.GetEnvironmentVariable("CLAUDE_MANAGER_DEMO") == "1" || !OperatingSystem.IsMacOS();

    public static AppState State()
    {
        var s = new AppState { FirstRunDone = true };
        s.Profiles.Add(new Profile { Id = "demo0", Name = "Default", Color = "#828282", IsDefault = true, DesktopSignedIn = true });
        s.Profiles.Add(new Profile
        {
            Id = "demo1", Name = "Work", Color = "#2F80ED", Icon = "emoji:💼", DesktopSignedIn = true,
            DesktopDataDir = "/Users/you/Library/Application Support/Claude-Work", CliConfigDir = "/Users/you/.claude-work",
            RecentDirs = { "/Users/you/code/acme-api", "/Users/you/code/design-system" },
        });
        s.Profiles.Add(new Profile
        {
            Id = "demo2", Name = "Personal", Color = "#D97757", Icon = "emoji:🏠", DesktopSignedIn = true,
            DesktopDataDir = "/Users/you/Library/Application Support/Claude-Personal", CliConfigDir = "/Users/you/.claude-personal",
        });
        s.Profiles.Add(new Profile
        {
            Id = "demo3", Name = "Client A", Color = "#27AE60", DesktopSignedIn = false,
            DesktopDataDir = "/Users/you/Library/Application Support/Claude-Client-a", CliConfigDir = "/Users/you/.claude-client-a",
        });
        return s;
    }

    public static Dictionary<string, long> Usage() => new() { ["demo1"] = 760L << 20, ["demo2"] = 486L << 20 };

    public static MacDesktop Desktop => new("/Applications/Claude.app", "1.0.0");
    public static MacCli Cli => new(true, "/opt/homebrew/bin/claude", new Version(2, 1, 285), "2.1.285 (Claude Code)");
}
