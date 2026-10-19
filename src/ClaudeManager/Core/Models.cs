using System.Text.Json.Serialization;

namespace ClaudeManager.Core;

public enum FolderLayout { UserProfile, AccountsParent }

public sealed class Profile
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#D97757";

    /// <summary>
    /// Profile icon: null or "claude" = the Claude app icon with a badge in the profile color,
    /// "preset:&lt;name&gt;" = a built-in glyph in the profile color, "file:&lt;path&gt;" = an imported image
    /// (copied into Claude Manager's icons folder).
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>The user's existing setup: launched without --user-data-dir or a profile CLAUDE_CONFIG_DIR.</summary>
    public bool IsDefault { get; set; }

    public string? DesktopDataDir { get; set; }
    public string? CliConfigDir { get; set; }
    public bool DesktopSignedIn { get; set; }
    public List<string> RecentDirs { get; set; } = new();

    /// <summary>Folders Claude Manager created itself. Only these can ever be deleted.</summary>
    public List<string> CreatedFolders { get; set; } = new();

    [JsonIgnore] public string DesktopTitle => $"Claude ({Name})";
    [JsonIgnore] public string TerminalTitle => $"Claude Code ({Name})";

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public void AddRecentDir(string dir)
    {
        RecentDirs.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
        RecentDirs.Insert(0, dir);
        if (RecentDirs.Count > 8) RecentDirs.RemoveRange(8, RecentDirs.Count - 8);
    }
}

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;
    public bool FirstRunDone { get; set; }
    public FolderLayout Layout { get; set; } = FolderLayout.UserProfile;
    public bool AlwaysClearApiKey { get; set; }
    public bool LabelWindows { get; set; } = true;
    public bool LabelTrayIcons { get; set; } = true;
    public bool ShowMemory { get; set; } = true;

    /// <summary>Send claude:// links (sign-in callbacks etc.) through Claude Manager to the right profile.</summary>
    public bool RouteLinks { get; set; } = true;

    /// <summary>Claude's own claude:// handler command, written back when routing is turned off.</summary>
    public string? SavedProtocolCommand { get; set; }

    /// <summary>Profile waiting for its first desktop sign-in (kept across restarts so links still reach it).</summary>
    public string? PendingSignInId { get; set; }
    public DateTime? PendingSignInUtc { get; set; }

    /// <summary>A pending sign-in older than this is ignored (the user gave up or finished without confirming).</summary>
    public static readonly TimeSpan PendingSignInLifetime = TimeSpan.FromMinutes(30);

    public List<Profile> Profiles { get; set; } = new();

    public Profile? Find(string id) => Profiles.FirstOrDefault(p => p.Id == id);
}

public sealed record LaunchResult(bool Ok, string Message, string Detail);
