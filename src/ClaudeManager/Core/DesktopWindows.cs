using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeManager.Core;

/// <summary>A running Claude desktop main process and the data folder it was started with (null = app default).</summary>
public sealed record DesktopInstance(int Pid, string? UserDataDir);

/// <summary>
/// Finds running Claude desktop instances, brings a hidden/minimized instance back to the front,
/// and labels window titles with the profile name. Only window state/titles are touched, never Claude's files.
/// </summary>
public static class DesktopWindows
{
    /// <summary>Pid, image path and command line of processes with the given image name (WMI, 5 s timeout).</summary>
    public static List<(int Pid, string Path, string CommandLine)> QueryProcesses(string imageName)
    {
        var list = new List<(int, string, string)>();
        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                new ObjectQuery($"SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = '{imageName.Replace("'", "")}'"), options);
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
                using (mo)
                    list.Add((Convert.ToInt32(mo["ProcessId"]), mo["ExecutablePath"] as string ?? "", mo["CommandLine"] as string ?? ""));
        }
        catch (Exception ex) { Log.Error($"Listing {imageName} processes failed: {ex.Message}"); }
        return list;
    }

    public static List<DesktopInstance> FindInstances() =>
        QueryProcesses("claude.exe")
            .Where(x => !x.CommandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase)) // Chromium helpers
            .Where(x => IsDesktopBinary(x.Path))                                             // not the Claude Code CLI
            .Select(x => new DesktopInstance(x.Pid, ParseUserDataDir(x.CommandLine)))
            .ToList();

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    static readonly Dictionary<int, bool> desktopPidCache = new();
    static readonly object cacheGate = new();

    /// <summary>Keeps only pids whose image is the Claude desktop app. Results are cached per pid (bounded to live pids).</summary>
    public static List<int> FilterDesktopPids(IReadOnlyCollection<int> pids)
    {
        lock (cacheGate)
        {
            foreach (var stale in desktopPidCache.Keys.Where(k => !pids.Contains(k)).ToList()) desktopPidCache.Remove(stale);
            var result = new List<int>();
            foreach (var pid in pids)
            {
                if (!desktopPidCache.TryGetValue(pid, out var isDesktop))
                {
                    isDesktop = false;
                    var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            var sb = new StringBuilder(1024);
                            int size = sb.Capacity;
                            if (QueryFullProcessImageName(h, 0, sb, ref size)) isDesktop = IsDesktopBinary(sb.ToString());
                        }
                        finally { CloseHandle(h); }
                    }
                    desktopPidCache[pid] = isDesktop;
                }
                if (isDesktop) result.Add(pid);
            }
            return result;
        }
    }

    static bool IsDesktopBinary(string f) =>
        f.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase)
        || f.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(@"\Claude\Claude.exe", StringComparison.OrdinalIgnoreCase);

    public static string? ParseUserDataDir(string cmd)
    {
        const string flag = "--user-data-dir=";
        var i = cmd.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = cmd[(i + flag.Length)..];
        string value = rest.StartsWith('"')
            ? rest[1..].Split('"')[0]
            : rest.Split(' ')[0];
        try { return Paths.Normalize(value); } catch { return value; }
    }

    /// <summary>Which running instance belongs to this profile (default profile = instance without the flag).</summary>
    public static DesktopInstance? FindForProfile(Profile p, IEnumerable<DesktopInstance> instances)
    {
        if (p.IsDefault) return instances.FirstOrDefault(i => i.UserDataDir == null
            || i.UserDataDir.Equals(Paths.Normalize(Paths.DefaultDesktopDir), StringComparison.OrdinalIgnoreCase));
        if (p.DesktopDataDir == null) return null;
        var want = Paths.Normalize(p.DesktopDataDir);
        return instances.FirstOrDefault(i => i.UserDataDir != null && i.UserDataDir.Equals(want, StringComparison.OrdinalIgnoreCase));
    }

    public static Profile? ProfileFor(DesktopInstance inst, IEnumerable<Profile> profiles) =>
        profiles.FirstOrDefault(p => FindForProfile(p, new[] { inst }) != null);

    // ---------- windows ----------

    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    // ShowWindowAsync / SendMessageTimeout: never block on another process's window, even if Claude hangs.
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);
    const uint WM_SETTEXT = 0x000C, SMTO_ABORTIFHUNG = 0x0002, SMTO_BLOCK = 0x0001;
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    const int SW_SHOW = 5, SW_RESTORE = 9;
    const uint GW_OWNER = 4;

    sealed record Win(IntPtr Handle, string Title, bool Visible, long Area);

    static List<Win> WindowsOf(int pid)
    {
        var result = new List<Win>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p != pid || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (!cls.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return true;
            var title = new StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            if (!title.ToString().StartsWith("Claude", StringComparison.Ordinal)) return true;
            GetWindowRect(h, out var r);
            result.Add(new Win(h, title.ToString(), IsWindowVisible(h), (long)(r.Right - r.Left) * (r.Bottom - r.Top)));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>The app's main window: the visible one if any, else the largest hidden one (closed to tray).</summary>
    static Win? MainWindow(int pid)
    {
        var wins = WindowsOf(pid);
        return wins.Where(w => w.Visible).OrderByDescending(w => w.Area).FirstOrDefault()
               ?? wins.Where(w => w.Area > 200 * 200).OrderByDescending(w => w.Area).FirstOrDefault();
    }

    /// <summary>Ends one instance (its whole process tree) and waits for it. Other profiles are untouched.</summary>
    public static bool EndInstance(DesktopInstance inst, TimeSpan timeout)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(inst.Pid);
            proc.Kill(entireProcessTree: true);
            var ok = proc.WaitForExit(timeout);
            Thread.Sleep(500); // let Windows release file handles
            Log.Info($"Ended Claude desktop pid={inst.Pid} ({inst.UserDataDir}) ok={ok}");
            return ok;
        }
        catch (ArgumentException) { return true; } // already gone
        catch (Exception ex) { Log.Error($"Ending pid={inst.Pid} failed: {ex.Message}"); return false; }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    /// <summary>Process id owning the foreground window (0 if none).</summary>
    public static int ForegroundPid()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(h, out var pid);
        return pid;
    }

    public enum FrontResult { Restored, NoWindow, NotResponding }

    /// <summary>Shows, restores and focuses the instance's main window without ever blocking on it.</summary>
    public static FrontResult BringToFront(DesktopInstance inst)
    {
        var w = MainWindow(inst.Pid);
        if (w == null) return FrontResult.NoWindow;
        if (IsHungAppWindow(w.Handle)) return FrontResult.NotResponding;
        ShowWindowAsync(w.Handle, IsIconic(w.Handle) ? SW_RESTORE : SW_SHOW);
        SetForegroundWindow(w.Handle);
        return FrontResult.Restored;
    }

    /// <summary>Sets "Claude (Name)" on the visible main window(s) of the instance when Claude reset it.</summary>
    public static void Label(DesktopInstance inst, Profile p)
    {
        var suffix = $" ({p.Name})";
        foreach (var w in WindowsOf(inst.Pid).Where(w => w.Visible))
        {
            if (w.Title.EndsWith(suffix, StringComparison.Ordinal) || IsHungAppWindow(w.Handle)) continue;
            SendMessageTimeout(w.Handle, WM_SETTEXT, IntPtr.Zero, w.Title + suffix, SMTO_ABORTIFHUNG | SMTO_BLOCK, 500, out _);
        }
    }
}
