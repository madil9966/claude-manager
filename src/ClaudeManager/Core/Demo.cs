namespace ClaudeManager.Core;

/// <summary>
/// CLAUDE_MANAGER_DEMO=1: sample profiles and memory figures for screenshots. Nothing is read from or
/// written to the real profile list, the registry (link routing, startup) or shortcuts, and no folders are created.
/// </summary>
public static class Demo
{
    public static bool On { get; } = Environment.GetEnvironmentVariable("CLAUDE_MANAGER_DEMO") == "1";

    public static AppState State()
    {
        var s = new AppState { FirstRunDone = true, RouteLinks = true };
        s.Profiles.Add(new Profile { Id = "demo0", Name = "Default", Color = "#828282", IsDefault = true, DesktopSignedIn = true });
        s.Profiles.Add(new Profile
        {
            Id = "demo1", Name = "Work", Color = "#2F80ED", Icon = "preset:work", DesktopSignedIn = true,
            DesktopDataDir = @"C:\Users\you\AppData\Roaming\Claude-Work", CliConfigDir = @"C:\Users\you\.claude-work",
            RecentDirs = { @"C:\Users\you\source\acme-api", @"C:\Users\you\source\design-system" },
        });
        s.Profiles.Add(new Profile
        {
            Id = "demo2", Name = "Personal", Color = "#D97757", DesktopSignedIn = true,
            DesktopDataDir = @"C:\Users\you\AppData\Roaming\Claude-Personal", CliConfigDir = @"C:\Users\you\.claude-personal",
        });
        s.Profiles.Add(new Profile
        {
            Id = "demo3", Name = "Client A", Color = "#27AE60", Icon = "preset:initial", DesktopSignedIn = false,
            DesktopDataDir = @"C:\Users\you\AppData\Roaming\Claude-Client-a", CliConfigDir = @"C:\Users\you\.claude-client-a",
        });
        return s;
    }

    public static UsageSnapshot Usage()
    {
        const long MB = 1024 * 1024;
        var map = new Dictionary<string, ProfileUsage>
        {
            ["demo1"] = new("demo1", true, 612 * MB, 1, 148 * MB),
            ["demo2"] = new("demo2", true, 486 * MB, 0, 0),
        };
        return new UsageSnapshot(map, map.Values.Sum(u => u.TotalBytes), DateTime.Now);
    }
}
