using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ClaudeManager.Setup;

public sealed record InstallOptions(string Dir, bool DesktopShortcut, bool StartMenuShortcut, bool StartWithWindows);

public sealed record ExistingInstall(string Dir, string? Version);

/// <summary>
/// Per-user install: no admin rights, everything under the user's profile and HKCU.
/// Names below must match the app (Core/Install.cs, Core/Shell.cs).
/// </summary>
public static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeManager";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ClaudeManager";
    const string ExeName = "ClaudeManager.exe";
    const string ShortcutName = "Claude Manager.lnk";
    static readonly string[] PayloadFiles = { ExeName, "LICENSE.txt", "README.md" };

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Claude Manager");

    public static ExistingInstall? FindExisting()
    {
        using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (k?.GetValue("InstallLocation") is not string dir || dir.Length == 0) return null;
        return new ExistingInstall(dir, k.GetValue("DisplayVersion") as string);
    }

    /// <summary>Null when the folder is usable, else why not.</summary>
    public static string? CheckDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) return "Choose an install folder.";
            if (dir.StartsWith(@"\\")) return "Choose a folder on a local drive.";
            if (!Path.IsPathFullyQualified(dir)) return "Enter a full path, like C:\\Users\\you\\AppData\\Local\\Programs\\Claude Manager.";
            var full = Path.GetFullPath(dir).TrimEnd('\\');
            if (full.Length <= 3) return "Don't install directly into a drive root. Choose a folder.";
            foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows })
            {
                var sys = Environment.GetFolderPath(f);
                if (sys.Length > 0 && (full.Equals(sys, StringComparison.OrdinalIgnoreCase) || full.StartsWith(sys + "\\", StringComparison.OrdinalIgnoreCase)))
                    return "That folder needs administrator rights. Claude Manager installs for your user only; keep the suggested folder or choose one in your user folder.";
            }
            foreach (var broad in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments })
                if (full.Equals(Environment.GetFolderPath(broad), StringComparison.OrdinalIgnoreCase))
                    return "Choose a dedicated folder (for example add \\Claude Manager at the end).";
            if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !File.Exists(Path.Combine(full, ExeName)))
                return "That folder already contains other files. Choose an empty or new folder.";
            return null;
        }
        catch (Exception ex) { return $"That folder can't be used: {ex.Message}"; }
    }

    public static long PayloadBytes()
    {
        long total = 0;
        foreach (var f in PayloadFiles)
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload/" + f))
                total += s?.Length ?? 0;
        return total;
    }

    public static string Install(InstallOptions o, Action<string> report)
    {
        var dir = Path.GetFullPath(o.Dir).TrimEnd('\\');
        var exe = Path.Combine(dir, ExeName);

        report("Closing Claude Manager if it is running…");
        StopRunningApp(TimeSpan.FromSeconds(8));

        report($"Copying files to {dir}…");
        Directory.CreateDirectory(dir);
        foreach (var f in PayloadFiles)
        {
            using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload/" + f)
                            ?? throw new InvalidOperationException($"Setup is damaged: {f} is missing. Download it again.");
            var dest = Path.Combine(dir, f);
            var tmp = dest + ".new";
            using (var fs = File.Create(tmp)) src.CopyTo(fs);
            if (File.Exists(dest))
            {
                // A running/locked exe can still be renamed; the old copy is removed now or on the next update.
                var old = dest + ".old";
                try { File.Delete(old); } catch { }
                try { File.Move(dest, old); } catch { }
            }
            File.Move(tmp, dest, overwrite: true);
            try { File.Delete(dest + ".old"); } catch { }
        }

        report("Creating shortcuts…");
        var startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName);
        var desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);
        SetShortcut(startMenuLnk, o.StartMenuShortcut ? exe : null);
        SetShortcut(desktopLnk, o.DesktopShortcut ? exe : null);

        report("Applying settings…");
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (o.StartWithWindows) run.SetValue(RunValue, $"\"{exe}\" --tray");
            else if (run.GetValue(RunValue) is string v && v.Contains(ExeName, StringComparison.OrdinalIgnoreCase)) run.DeleteValue(RunValue, false);
        }

        report("Registering with Apps & Features…");
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Claude Manager (unofficial)");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Claude Manager community project, not affiliated with Anthropic");
            k.SetValue("Comments", "Unofficial community utility for running several Claude accounts. Not endorsed by Anthropic.");
            k.SetValue("InstallLocation", dir);
            k.SetValue("DisplayIcon", $"\"{exe}\",0");
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
            k.SetValue("URLInfoAbout", "https://github.com/madil9966/claude-manager");
            k.SetValue("HelpLink", "https://github.com/madil9966/claude-manager/issues");
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("EstimatedSize", (int)Math.Max(1, PayloadBytes() / 1024), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        report("Done.");
        return exe;
    }

    public static void Launch(string exe)
    {
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
    }

    // ---------- helpers ----------

    /// <summary>Creates/updates the shortcut, or removes it when target is null (only if it points to Claude Manager).</summary>
    static void SetShortcut(string lnk, string? target)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(t)!;
        try
        {
            if (target == null)
            {
                if (!File.Exists(lnk)) return;
                dynamic existing = shell.CreateShortcut(lnk);
                string current = existing.TargetPath;
                Marshal.FinalReleaseComObject(existing);
                if (Path.GetFileName(current).Equals(ExeName, StringComparison.OrdinalIgnoreCase)) File.Delete(lnk);
                return;
            }
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.Arguments = "";
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.IconLocation = target + ",0";
            sc.Description = "Claude Manager (unofficial): run several Claude accounts side by side";
            sc.Save();
            Marshal.FinalReleaseComObject(sc);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>Asks a running Claude Manager to exit (same pipe protocol as the app), then ends it if needed.</summary>
    static void StopRunningApp(TimeSpan timeout)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var mutexName = @"Local\ClaudeManager-" + Environment.UserName;
        bool Running() { if (Mutex.TryOpenExisting(mutexName, out var m)) { m.Dispose(); return true; } return false; }
        if (!Running()) return;

        foreach (var pipe in new[] { $"ClaudeManager-{Environment.UserName}-{session}", $"ClaudeManager-{Environment.UserName}" /* versions before 1.0 */ })
        {
            try
            {
                using var c = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
                c.Connect(1500);
                using var w = new StreamWriter(c);
                w.WriteLine(JsonSerializer.Serialize(new[] { "--exit" }));
                w.Flush();
            }
            catch { /* not this pipe */ }
        }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && Running()) Thread.Sleep(200);
        if (!Running()) return;

        // Older versions don't know --exit, or it is hung: end it (this session only).
        foreach (var p in Process.GetProcessesByName("ClaudeManager"))
            using (p)
                if (p.SessionId == session)
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
    }
}
