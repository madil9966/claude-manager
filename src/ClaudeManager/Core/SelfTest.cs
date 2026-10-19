namespace ClaudeManager.Core;

/// <summary>
/// Run with: ClaudeManager.exe --selftest > result.txt. Checks the safety rules without touching any data.
/// To test path resolution it briefly creates a junction in %TEMP% and a subst drive letter, and removes
/// both again; nothing is ever written through them.
/// </summary>
public static class SelfTest
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetShortPathName(string longPath, System.Text.StringBuilder shortPath, int size);

    static string? ShortPath(string path)
    {
        if (!Directory.Exists(path)) return null;
        var sb = new System.Text.StringBuilder(1024);
        return GetShortPathName(path, sb, sb.Capacity) > 0 ? sb.ToString() : null;
    }

    static bool MakeJunction(string link, string target)
    {
        try
        {
            var (code, _) = Proc.Run("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"", 10000);
            return code == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    public static int Run(TextWriter o)
    {
        int fails = 0;
        void Check(string name, bool ok) { o.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}"); if (!ok) fails++; }
        string H(string rel) => Path.Combine(Paths.UserProfile, rel);
        string A(string rel) => Path.Combine(Paths.RoamingAppData, rel);

        Check("~/.claude rejected as profile folder", SafetyGuard.CheckProfileFolder(H(".claude")) != null);
        Check("~/.claude/sub rejected", SafetyGuard.CheckProfileFolder(H(@".claude\sub")) != null);
        Check("~/.claude.json rejected", SafetyGuard.CheckProfileFolder(H(".claude.json")) != null);
        Check("%APPDATA%/Claude rejected", SafetyGuard.CheckProfileFolder(A("Claude")) != null);
        Check("%APPDATA%/Claude/x rejected", SafetyGuard.CheckProfileFolder(A(@"Claude\x")) != null);
        Check("user folder rejected (contains ~/.claude)", SafetyGuard.CheckProfileFolder(Paths.UserProfile) != null);
        Check("%APPDATA% rejected", SafetyGuard.CheckProfileFolder(Paths.RoamingAppData) != null);
        Check("trailing slash/case tricks rejected", SafetyGuard.CheckProfileFolder(H(@".CLAUDE\")) != null);
        Check("relative path rejected", SafetyGuard.CheckProfileFolder(@"..\.claude-x") != null);
        Check("~/.claude-work allowed", SafetyGuard.CheckProfileFolder(H(".claude-work")) == null);
        Check("%APPDATA%/Claude-Work allowed", SafetyGuard.CheckProfileFolder(A("Claude-Work")) == null);
        Check(".claude-accounts/work/cli allowed", SafetyGuard.CheckProfileFolder(H(@".claude-accounts\work\cli")) == null);
        Check(".claude-accounts not deletable", SafetyGuard.CheckDeletable(Paths.AccountsParent) != null);
        Check("drive root not deletable", SafetyGuard.CheckDeletable(@"C:\") != null);
        Check("~/.claude not deletable", SafetyGuard.CheckDeletable(H(".claude")) != null);
        Check("~/.claude-work deletable by rule", SafetyGuard.CheckDeletable(H(".claude-work")) == null);

        bool Blocked(string p) { try { SafetyGuard.AssertWritable(p); return false; } catch (UnauthorizedAccessException) { return true; } }
        Check("write to ~/.claude/settings.json blocked", Blocked(H(@".claude\settings.json")));
        Check("write to ~/.claude.json blocked", Blocked(H(".claude.json")));
        Check("write to %APPDATA%/Claude/config.json blocked", Blocked(A(@"Claude\config.json")));
        Check("write to profiles.json allowed", !Blocked(Paths.ProfilesFile));

        // Paths that only lead to protected data once Windows resolves them.
        Check("network path rejected", SafetyGuard.CheckProfileFolder(@"\\localhost\c$\Users\x\.claude-work") != null);
        Check("device path rejected", SafetyGuard.CheckProfileFolder(@"\\?\C:\Users\x\.claude-work") != null);
        var shortAppData = ShortPath(Paths.DefaultDesktopDir);
        if (shortAppData != null && !shortAppData.Equals(Paths.DefaultDesktopDir, StringComparison.OrdinalIgnoreCase))
        {
            Check($"8.3 short name rejected ({shortAppData})", SafetyGuard.CheckProfileFolder(Path.Combine(shortAppData, "x")) != null);
            Check("write via 8.3 short name blocked", Blocked(Path.Combine(shortAppData, "config.json")));
        }
        else o.WriteLine("SKIP  8.3 short names (disabled on this drive or folder missing)");

        // A junction higher up in the path that points at protected data. Only the link is created
        // (in %TEMP%) and removed again; nothing is written through it.
        var junction = Path.Combine(Path.GetTempPath(), "cm-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        var target = Directory.Exists(Paths.DefaultDesktopDir) ? Paths.DefaultDesktopDir : Paths.ExistingCliDir;
        if (Directory.Exists(target) && MakeJunction(junction, target))
        {
            try
            {
                Check("folder under a junction to protected data rejected", SafetyGuard.CheckProfileFolder(Path.Combine(junction, @"sub\profile")) != null);
                Check("write under a junction to protected data blocked", Blocked(Path.Combine(junction, "new.json")));
            }
            finally { try { Directory.Delete(junction); } catch { } }
            Check("selftest junction removed (link only)", !Directory.Exists(junction) && Directory.Exists(target));
        }
        else o.WriteLine("SKIP  junction test (could not create a junction)");

        // A subst drive letter mapped onto %APPDATA% (removed again right after).
        var free = "QRSTUVWXYZ".Select(c => $"{c}:").FirstOrDefault(d => !Directory.Exists(d + "\\"));
        if (free != null && Proc.Run("subst.exe", $"{free} \"{Paths.RoamingAppData}\"", 10000).ExitCode == 0)
        {
            try
            {
                Check($"subst drive onto %APPDATA% rejected ({free}\\Claude\\x)", SafetyGuard.CheckProfileFolder($@"{free}\Claude\x") != null);
                Check("write via subst drive blocked", Blocked($@"{free}\Claude\config.json"));
            }
            finally { Proc.Run("subst.exe", $"{free} /D", 10000); }
        }
        else o.WriteLine("SKIP  subst test (no free drive letter)");

        // Profiles must not nest inside each other (deleting one would wipe the other).
        var nest = new AppState();
        var outer = new Profile { Name = "Outer", DesktopDataDir = H(@".claude-accounts\outer\desktop"), CliConfigDir = H(@".claude-accounts\outer\cli") };
        nest.Profiles.Add(outer);
        Check("folder inside another profile's folder rejected", ProfileService.OverlappingProfile(nest, H(@".claude-accounts\outer\cli\inner"), null) == outer);
        Check("folder containing another profile's folder rejected", ProfileService.OverlappingProfile(nest, H(@".claude-accounts\outer"), null) == outer);
        Check("separate profile folder allowed", ProfileService.OverlappingProfile(nest, H(@".claude-accounts\other\cli"), null) == null);
        Check("own folders don't conflict when editing", ProfileService.OverlappingProfile(nest, outer.CliConfigDir!, outer) == null);
        // Backups: secrets in JSON settings are replaced, ordinary settings kept.
        var cfg = System.Text.Json.Nodes.JsonNode.Parse("""
            { "primaryApiKey": "sk-ant-api03-SECRET1",
              "theme": "dark", "tokenCount": 12, "hasCompletedOnboarding": true,
              "customApiKeyResponses": { "approved": ["SECRET2fragment"] },
              "env": { "ANTHROPIC_API_KEY": "SECRET3", "DEBUG": "1" },
              "mcpServers": { "gh": { "command": "npx", "args": ["-y", "server", "--api-key", "SECRET4", "--token=SECRET5", "ghp_SECRET6abcdefghijklmnopqrstu"],
                                      "env": { "GITHUB_TOKEN": "SECRET7" }, "headers": { "Authorization": "Bearer SECRET8" } } },
              "projects": { "C:/x": { "mcpServers": { "s": { "env": { "KEY": "SECRET9" } } }, "allowedTools": ["Bash"] } } }
            """);
        int scrubbed = SecretScrubber.Scrub(cfg);
        var outJson = cfg!.ToJsonString();
        Check($"backup removes all secret values ({scrubbed} replaced)", !outJson.Contains("SECRET") && scrubbed == 10);
        Check("backup keeps ordinary settings", outJson.Contains("\"theme\":\"dark\"") && outJson.Contains("\"tokenCount\":12") && outJson.Contains("Bash") && outJson.Contains("\"command\":\"npx\""));

        Check("IPC pipe is per Windows session", Ipc.PipeName.EndsWith("-" + System.Diagnostics.Process.GetCurrentProcess().SessionId));
        Check("uninstall only deletes known program files", Install.InstalledFiles.All(f => !f.Contains('\\') && !f.Contains('*')));
        Check("install folder deletable by rule", SafetyGuard.CheckDeletable(Path.Combine(Paths.LocalAppData, "Programs", "Claude Manager")) == null);
        Check("shared Programs folder never deletable", SafetyGuard.CheckDeletable(Path.Combine(Paths.LocalAppData, "Programs")) != null);
        Check("unknown Claude Code version is not trusted", new CliInstall(true, "claude", null, "timed out").Unverified);

        var state = new AppState();
        var (d, c) = ProfileService.SuggestFolders(state, "Work", FolderLayout.UserProfile);
        Check($"suggested desktop {d}", SafetyGuard.CheckProfileFolder(d) == null && d.EndsWith("Claude-Work"));
        Check($"suggested cli {c}", SafetyGuard.CheckProfileFolder(c) == null && c.EndsWith(".claude-work"));

        var def = ProfileService.CreateDefault("Default");
        Check("default desktop launch has no flag", DesktopApp.BuildArgs(def) == "");
        var work = new Profile { Name = "Work", DesktopDataDir = d, CliConfigDir = c };
        Check("work desktop launch uses --user-data-dir", DesktopApp.BuildArgs(work) == $"--user-data-dir=\"{d}\"");
        var script = Terminal.BuildScript(work, true, "claude");
        Check("work script sets CLAUDE_CONFIG_DIR", script.Contains($"set \"CLAUDE_CONFIG_DIR={c}\""));
        Check("work script clears API key when asked", script.Contains("set \"ANTHROPIC_API_KEY=\""));
        var dscript = Terminal.BuildScript(def, false, "claude");
        Check("default script keeps existing setup", Paths.GlobalClaudeConfigDir == null ? dscript.Contains("set \"CLAUDE_CONFIG_DIR=\"") : dscript.Contains(Paths.GlobalClaudeConfigDir));

        const string link = "claude://login/callback?code=SECRET&state=x";
        Check("link goes to work profile folder", DesktopApp.BuildArgs(work, link) == $"--user-data-dir=\"{d}\" \"{link}\"");
        Check("link for default profile has no flag", DesktopApp.BuildArgs(def, link) == $"\"{link}\"");
        Check("valid claude:// link accepted", ProtocolHandler.IsValidUrl(link));
        Check("non-claude link rejected", !ProtocolHandler.IsValidUrl("https://evil.example/") && !ProtocolHandler.IsValidUrl("--inject"));
        Check("link with quotes rejected", !ProtocolHandler.IsValidUrl("claude://x\" --user-data-dir=C:\\"));
        Check("logged link hides sign-in code", !ProtocolHandler.Redact(link).Contains("SECRET"));
        Check("Claude Manager handler recognised", ProtocolHandler.IsOurs(ProtocolHandler.OurCommand) && !ProtocolHandler.IsOurs("\"C:\\x\\claude.exe\" \"%1\""));
        var handler = ProtocolHandler.Read();
        Console.Out.WriteLine($"INFO  claude:// handler: {handler ?? "(not registered for this user)"}");

        Check("parse quoted --user-data-dir", DesktopWindows.ParseUserDataDir($"\"x\\claude.exe\" --user-data-dir=\"{d}\"") == d);
        Check("parse unquoted --user-data-dir", DesktopWindows.ParseUserDataDir($"claude.exe --user-data-dir={d} --foo") == d);
        Check("no flag = default instance", DesktopWindows.ParseUserDataDir("\"claude.exe\"") == null);
        var insts = new[] { new DesktopInstance(1, null), new DesktopInstance(2, d) };
        Check("default profile maps to unflagged instance", DesktopWindows.FindForProfile(def, insts)?.Pid == 1);
        Check("work profile maps to its folder instance", DesktopWindows.FindForProfile(work, insts)?.Pid == 2);

        // Process snapshot must agree with what Windows reports through the normal API.
        var snap = ProcessSnapshot.Take();
        using (var me = System.Diagnostics.Process.GetCurrentProcess())
        {
            Check($"snapshot lists {snap.Count} processes", snap.Count > 20);
            Check("snapshot finds this process by pid", snap.TryGetValue(me.Id, out var mine));
            if (mine != null)
            {
                Check("snapshot image name correct", mine.Name.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase));
                me.Refresh();
                Check($"snapshot memory plausible ({mine.PrivateWorkingSet / 1048576} MB private of {me.WorkingSet64 / 1048576} MB working set)",
                      mine.PrivateWorkingSet > 0 && mine.PrivateWorkingSet <= me.WorkingSet64 * 1.1);
                Check("snapshot parent pid exists", snap.ContainsKey(mine.ParentPid));
            }
        }
        Check("format GB", UsageMonitor.Format(3L << 29) == "1.5 GB");
        Check("format MB", UsageMonitor.Format(300L << 20) == "300 MB");
        Check("describe usage", UsageMonitor.Describe(new ProfileUsage("x", true, 1L << 30, 2, 200L << 20)) == "● Desktop 1.0 GB · 2 terminals 200 MB");

        // Atomic delete: a file in use must cancel the whole delete (nothing removed), then succeed once released.
        var root = Path.Combine(Path.GetTempPath(), "cm-selftest-" + Guid.NewGuid().ToString("N")[..6]);
        var tDesk = Path.Combine(root, "desk");
        var tCli = Path.Combine(root, "cli");
        try
        {
            var st = new AppState();
            var tp = ProfileService.Create(st, "T", "#000000", tDesk, tCli);
            File.WriteAllText(Path.Combine(tCli, "settings.json"), "{}");
            Directory.CreateDirectory(Path.Combine(tDesk, "Cache"));
            var locked = Path.Combine(tDesk, "Cache", "journal.baj");
            bool cancelled = false;
            using (new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                try { ProfileService.Delete(st, tp, true); } catch (ProfileInUseException) { cancelled = true; }
            }
            Check("delete cancelled while a file is in use", cancelled);
            Check("nothing removed when cancelled", File.Exists(Path.Combine(tCli, "settings.json")) && File.Exists(locked) && st.Profiles.Contains(tp));
            var notes = ProfileService.Delete(st, tp, true);
            Check("delete succeeds once released", !Directory.Exists(tDesk) && !Directory.Exists(tCli) && notes.Count == 0);
        }
        catch (Exception ex) { Check($"atomic delete test crashed: {ex.Message}", false); }
        finally { try { Directory.Delete(root, true); } catch { } }

        o.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return fails;
    }
}
