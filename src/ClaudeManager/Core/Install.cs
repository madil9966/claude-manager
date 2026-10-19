using System.Diagnostics;
using Microsoft.Win32;

namespace ClaudeManager.Core;

/// <summary>
/// Install location (written by the installer), self-repair when the exe was moved, and uninstall.
/// Everything here is per-user (HKCU); profile data folders are never touched.
/// </summary>
public static class Install
{
    /// <summary>Keep in sync with ClaudeManager.Setup/Installer.cs.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeManager";

    public static string OwnExe => Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find Claude Manager's own path.");

    /// <summary>The folder the installer put Claude Manager in, or null for a portable copy.</summary>
    public static string? InstalledDir()
    {
        using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return k?.GetValue("InstallLocation") is string s && s.Length > 0 ? s : null;
    }

    public static bool IsInstalledCopy =>
        InstalledDir() is { } dir && SafetyGuard.IsSameOrInside(OwnExe, dir);

    static bool SamePath(string? a, string? b)
    {
        if (a == null || b == null) return false;
        try { return Paths.Normalize(a).Equals(Paths.Normalize(b), StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }

    // ---------- moved exe ----------

    /// <summary>
    /// If Claude Manager was moved (portable copy run from a new place), repoint what still points at the
    /// old, now missing, exe: the "Start with Windows" entry and Claude Manager's shortcuts. The claude://
    /// handler is re-applied by link routing itself. Entries pointing at another existing copy are left alone.
    /// </summary>
    public static List<string> RepairMovedExe()
    {
        var fixedItems = new List<string>();
        var me = OwnExe;
        try
        {
            var run = StartupRegistration.RegisteredExe();
            if (run != null && !SamePath(run, me) && !File.Exists(run))
            {
                StartupRegistration.SetFor(me);
                fixedItems.Add($"Start with Windows ({run} -> {me})");
            }
        }
        catch (Exception ex) { Log.Error($"Repairing startup entry failed: {ex.Message}"); }

        try
        {
            foreach (var (lnk, target) in Shortcuts.FindOwn())
            {
                if (SamePath(target, me) || File.Exists(target)) continue;
                try { Shortcuts.Retarget(lnk, me); fixedItems.Add($"shortcut {lnk}"); }
                catch (Exception ex) { Log.Error($"Repairing shortcut {lnk} failed: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Error($"Checking shortcuts failed: {ex.Message}"); }

        foreach (var f in fixedItems) Log.Info($"Claude Manager was moved; repointed {f}");
        return fixedItems;
    }

    // ---------- uninstall ----------

    public sealed record UninstallResult(List<string> Done, List<string> Problems, bool FilesScheduled);

    /// <summary>
    /// Undoes everything Claude Manager set up for this user. Never deletes profile folders, Claude's data,
    /// or anything outside Claude Manager's own files.
    /// </summary>
    public static UninstallResult Uninstall(bool removeSettings, string? claudeExe)
    {
        var done = new List<string>();
        var problems = new List<string>();

        // 1. claude:// links back to Claude.
        try
        {
            var current = ProtocolHandler.Read();
            if (ProtocolHandler.IsOurs(current))
            {
                var saved = ProfileStore.Load().SavedProtocolCommand;
                var original = ProtocolHandler.ExeOf(saved) != null ? saved
                    : claudeExe != null ? ProtocolHandler.ClaudeCommandFor(claudeExe) : null;
                if (original != null && ProtocolHandler.Restore(original)) done.Add("claude:// links handed back to Claude");
                else
                {
                    ProtocolHandler.RemoveOurs();
                    done.Add("claude:// link handler removed (Claude registers itself again the next time it starts)");
                }
            }
        }
        catch (Exception ex) { problems.Add($"claude:// handler: {ex.Message}"); }

        // 2. Start with Windows.
        try
        {
            var run = StartupRegistration.RegisteredExe();
            if (run != null && Path.GetFileName(run).Equals("ClaudeManager.exe", StringComparison.OrdinalIgnoreCase))
            {
                StartupRegistration.SetFor(null);
                done.Add("Start with Windows entry removed");
            }
        }
        catch (Exception ex) { problems.Add($"Startup entry: {ex.Message}"); }

        // 3. Shortcuts (only .lnk files that point to a ClaudeManager.exe).
        try
        {
            int n = 0;
            foreach (var (lnk, _) in Shortcuts.FindOwn())
            {
                try { File.Delete(lnk); n++; } catch (Exception ex) { problems.Add($"{lnk}: {ex.Message}"); }
            }
            if (Directory.Exists(Shortcuts.StartMenuFolder) && !Directory.EnumerateFileSystemEntries(Shortcuts.StartMenuFolder).Any())
                Directory.Delete(Shortcuts.StartMenuFolder);
            if (n > 0) done.Add($"{n} shortcut{(n == 1 ? "" : "s")} removed");
        }
        catch (Exception ex) { problems.Add($"Shortcuts: {ex.Message}"); }

        // 4. Installed files and the Apps & Features entry (installed copies only).
        bool scheduled = false;
        var dir = InstalledDir();
        if (dir != null && SafetyGuard.IsSameOrInside(OwnExe, dir))
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                done.Add("Removed from Apps & Features");
                scheduled = ScheduleFolderRemoval(dir);
                if (scheduled) done.Add($"Program files in {dir} will be removed in a few seconds");
                else problems.Add($"Could not remove {dir}; delete it by hand.");
            }
            catch (Exception ex) { problems.Add($"Program files: {ex.Message}"); }
        }

        Log.Info($"Uninstall: {string.Join("; ", done)}{(problems.Count > 0 ? " | problems: " + string.Join("; ", problems) : "")}");

        // 5. Last (logging would recreate it): Claude Manager's own settings folder
        //    (profile list, logs, launch scripts, icons). Profile data folders live elsewhere and are kept.
        if (removeSettings)
        {
            try
            {
                if (Directory.Exists(Paths.AppDir))
                {
                    SafetyGuard.AssertWritable(Paths.AppDir);
                    Directory.Delete(Paths.AppDir, recursive: true);
                    done.Add($"Settings and logs removed ({Paths.AppDir})");
                }
            }
            catch (Exception ex) { problems.Add($"Settings folder: {ex.Message}"); }
        }
        return new UninstallResult(done, problems, scheduled);
    }

    /// <summary>Files the installer puts in the install folder. Only these are deleted, then the folder if empty.</summary>
    public static readonly string[] InstalledFiles = { "ClaudeManager.exe", "LICENSE.txt", "README.md", "ClaudeManager.exe.old" };

    /// <summary>
    /// The running exe can't delete itself, so a hidden cmd waits for this process to exit, then removes
    /// only the known installed files and the folder if it is then empty (rmdir without /s).
    /// </summary>
    static bool ScheduleFolderRemoval(string dir)
    {
        if (SafetyGuard.CheckDeletable(dir) is { } reason) { Log.Error($"Not removing {dir}: {reason}"); return false; }
        var files = string.Join(" ", InstalledFiles.Select(f => $"\"{Path.Combine(dir, f)}\""));
        var script = $"/d /c ping -n 4 127.0.0.1 >nul & del /f /q {files} >nul 2>&1 & rmdir \"{dir}\" >nul 2>&1";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", script)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            })?.Dispose();
            return true;
        }
        catch (Exception ex) { Log.Error($"Scheduling removal of {dir} failed: {ex.Message}"); return false; }
    }

    /// <summary>Asks a running Claude Manager (this session) to exit and waits for it. Returns false if it's still running.</summary>
    public static bool StopRunningInstance(TimeSpan timeout)
    {
        var mutexName = @"Local\ClaudeManager-" + Environment.UserName;
        bool Running() { if (Mutex.TryOpenExisting(mutexName, out var m)) { m.Dispose(); return true; } return false; }
        if (!Running()) return true;
        Ipc.TrySend(new[] { Ipc.ExitCommand }, 2000);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!Running()) return true;
            Thread.Sleep(200);
        }
        // Still there (hung, or an older version without --exit): end other Claude Manager processes of this user session.
        var me = Environment.ProcessId;
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("ClaudeManager"))
            using (p)
                if (p.Id != me && p.SessionId == session)
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
        Thread.Sleep(300);
        return !Running();
    }
}
