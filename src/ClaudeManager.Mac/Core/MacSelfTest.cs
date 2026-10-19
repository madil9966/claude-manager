using ClaudeManager.Core;

namespace ClaudeManager.Mac.Core;

/// <summary>ClaudeManager --selftest: checks the macOS safety rules without touching any real data.</summary>
public static class MacSelfTest
{
    public static int Run(TextWriter o)
    {
        int fails = 0;
        void Check(string name, bool ok) { o.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}"); if (!ok) fails++; }
        string H(string rel) => Path.Combine(MacPaths.Home, rel);
        bool mac = OperatingSystem.IsMacOS();
        o.WriteLine($"INFO  platform: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");

        Check("~/.claude rejected", MacSafety.CheckProfileFolder(H(".claude")) != null);
        Check("~/.claude/sub rejected", MacSafety.CheckProfileFolder(H(".claude/sub")) != null);
        Check("~/.claude.json rejected", MacSafety.CheckProfileFolder(H(".claude.json")) != null);
        Check("Application Support/Claude rejected", MacSafety.CheckProfileFolder(MacPaths.DefaultDesktopDir) != null);
        Check("Application Support/Claude/x rejected", MacSafety.CheckProfileFolder(Path.Combine(MacPaths.DefaultDesktopDir, "x")) != null);
        Check("case tricks rejected (~/.CLAUDE)", MacSafety.CheckProfileFolder(H(".CLAUDE")) != null);
        Check("home folder rejected", MacSafety.CheckProfileFolder(MacPaths.Home) != null);
        Check("Application Support rejected", MacSafety.CheckProfileFolder(MacPaths.AppSupport) != null);
        Check("relative path rejected", MacSafety.CheckProfileFolder("claude-work") != null);
        Check("~/.claude-work allowed", MacSafety.CheckProfileFolder(H(".claude-work")) == null);
        Check("Application Support/Claude-Work allowed", MacSafety.CheckProfileFolder(Path.Combine(MacPaths.AppSupport, "Claude-Work")) == null);
        Check("~ shorthand understood", MacSafety.CheckProfileFolder("~/.claude") != null && MacSafety.CheckProfileFolder("~/.claude-x") == null);

        bool Blocked(string p) { try { MacSafety.AssertWritable(p); return false; } catch (UnauthorizedAccessException) { return true; } }
        Check("write to ~/.claude/settings.json blocked", Blocked(H(".claude/settings.json")));
        Check("write to Application Support/Claude/config.json blocked", Blocked(Path.Combine(MacPaths.DefaultDesktopDir, "config.json")));
        Check("write to own profiles.json allowed", !Blocked(MacPaths.ProfilesFile));

        // Symlink anywhere in the path that leads into protected data (macOS only: needs real symlinks).
        if (mac)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "cm-selftest-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tmp);
            var target = Directory.Exists(MacPaths.HomeClaudeDir) ? MacPaths.HomeClaudeDir : MacPaths.AppSupport;
            var link = Path.Combine(tmp, "link");
            try
            {
                Directory.CreateSymbolicLink(link, target);
                if (target == MacPaths.HomeClaudeDir)
                {
                    Check("folder under a symlink to ~/.claude rejected", MacSafety.CheckProfileFolder(Path.Combine(link, "sub/profile")) != null);
                    Check("write under a symlink to ~/.claude blocked", Blocked(Path.Combine(link, "new.json")));
                }
                else
                {
                    Check("folder via a symlink to Application Support/Claude rejected", MacSafety.CheckProfileFolder(Path.Combine(link, "Claude", "x")) != null);
                }
                Check("/tmp resolves to its real location", MacSafety.RealPath("/tmp").StartsWith("/private/tmp", StringComparison.Ordinal));
            }
            finally { try { Directory.Delete(link); Directory.Delete(tmp); } catch { } }
            Check("selftest symlink removed, target kept", !Directory.Exists(link) && Directory.Exists(target));
        }
        else o.WriteLine("SKIP  symlink tests (macOS only)");

        // Profiles must not nest.
        var state = new AppState();
        var outer = new Profile { Name = "Outer", DesktopDataDir = H(".accounts/outer/desktop"), CliConfigDir = H(".accounts/outer/cli") };
        state.Profiles.Add(outer);
        Check("folder inside another profile rejected", MacSafety.OverlappingProfile(state, H(".accounts/outer/cli/inner"), null) == outer);
        Check("folder containing another profile rejected", MacSafety.OverlappingProfile(state, H(".accounts"), null) == outer);
        Check("separate folder allowed", MacSafety.OverlappingProfile(state, H(".accounts/other"), null) == null);

        // Launch arguments and terminal script.
        var desk = new MacDesktop("/Applications/Claude.app", "1.0");
        var def = new Profile { Name = "Default", IsDefault = true };
        var work = new Profile { Id = "w1", Name = "Work", DesktopDataDir = H("Library/Application Support/Claude-Work"), CliConfigDir = H(".claude-work") };
        Check("default opens Claude without a data folder", string.Join(' ', MacClaude.OpenArgs(desk, def)) == "-a /Applications/Claude.app");
        Check("profile opens its own instance and folder", MacClaude.OpenArgs(desk, work).SequenceEqual(new[] { "-n", "-a", "/Applications/Claude.app", "--args", $"--user-data-dir={work.DesktopDataDir}" }));
        var script = MacClaude.BuildScript(work, "/Users/x/it's here");
        Check("terminal script sets CLAUDE_CONFIG_DIR", script.Contains($"export CLAUDE_CONFIG_DIR='{work.CliConfigDir}'"));
        Check("terminal script quotes folders safely", script.Contains("cd -- '/Users/x/it'\\''s here'"));
        Check("default terminal keeps the existing setup", !MacClaude.BuildScript(def, "/tmp").Contains("CLAUDE_CONFIG_DIR="));
        Check("parse --user-data-dir with spaces", MacClaude.ParseUserDataDir("/Applications/Claude.app/Contents/MacOS/Claude --user-data-dir=/Users/a/Library/Application Support/Claude-Work --foo") == MacPaths.Normalize("/Users/a/Library/Application Support/Claude-Work"));
        Check("no flag = default instance", MacClaude.ParseUserDataDir("/Applications/Claude.app/Contents/MacOS/Claude") == null);
        Check("old Claude Code is blocked", new MacCli(true, "claude", new Version(2, 1, 100), "").TooOld);
        Check("unknown Claude Code version is not trusted", new MacCli(true, "claude", null, "timed out").Unverified);

        // Profile list: damaged file falls back to the backup.
        var dir = Path.Combine(Path.GetTempPath(), "cm-store-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "profiles.json");
            var st = new AppState { FirstRunDone = true };
            st.Profiles.Add(new Profile { Name = "A" });
            MacStore.Save(st, file);
            MacStore.Save(st, file);           // creates .bak
            File.WriteAllText(file, "{ broken");
            Check("damaged profile list restored from backup", MacStore.Load(file).Profiles.Count == 1);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        o.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return fails == 0 ? 0 : 1;
    }
}
