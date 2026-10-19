using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ClaudeManager.Core;

public sealed record ProcInfo(int Pid, int ParentPid, string Name, long PrivateWorkingSet, long CreateTime);

/// <summary>
/// All processes in one cheap system call (NtQuerySystemInformation), including parent pid and
/// private working set, the same "Memory" figure Task Manager shows.
/// </summary>
public static class ProcessSnapshot
{
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
    const int SystemProcessInformation = 5;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    static int bufferSize = 1 << 20;

    public static Dictionary<int, ProcInfo> Take()
    {
        var result = new Dictionary<int, ProcInfo>();
        if (IntPtr.Size != 8) return result; // offsets below are for 64-bit processes
        while (true)
        {
            var buf = Marshal.AllocHGlobal(bufferSize);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buf, bufferSize, out var needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    bufferSize = Math.Max(bufferSize * 2, needed + 64 * 1024);
                    continue;
                }
                if (status != 0) return result;

                int offset = 0;
                while (true)
                {
                    var p = buf + offset;
                    long privateWs = Marshal.ReadInt64(p, 0x08);
                    long createTime = Marshal.ReadInt64(p, 0x20);
                    int nameBytes = (ushort)Marshal.ReadInt16(p, 0x38);
                    var namePtr = Marshal.ReadIntPtr(p, 0x40);
                    int pid = (int)Marshal.ReadIntPtr(p, 0x50);
                    int ppid = (int)Marshal.ReadIntPtr(p, 0x58);
                    var name = namePtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(namePtr, nameBytes / 2);
                    result[pid] = new ProcInfo(pid, ppid, name, privateWs, createTime);
                    int next = Marshal.ReadInt32(p, 0);
                    if (next == 0) break;
                    offset += next;
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}

/// <summary>A terminal opened by Claude Manager: cmd.exe running a profile's launcher script.</summary>
public sealed record TerminalSession(int CmdPid, string ProfileId);

public static class TerminalSessions
{
    public static List<TerminalSession> Find()
    {
        var list = new List<TerminalSession>();
        var pattern = new Regex(Regex.Escape(Paths.LaunchersDir) + @"\\([0-9a-zA-Z]+)\.cmd", RegexOptions.IgnoreCase);
        foreach (var (pid, _, cmd) in DesktopWindows.QueryProcesses("cmd.exe"))
        {
            var m = pattern.Match(cmd);
            if (m.Success) list.Add(new TerminalSession(pid, m.Groups[1].Value));
        }
        return list;
    }
}

public sealed record ProfileUsage(string ProfileId, bool DesktopRunning, long DesktopBytes, int Terminals, long TerminalBytes)
{
    public long TotalBytes => DesktopBytes + TerminalBytes;
}

public sealed record UsageSnapshot(IReadOnlyDictionary<string, ProfileUsage> ByProfile, long TotalBytes, DateTime At)
{
    public int RunningProfiles => ByProfile.Count;
}

public static class UsageMonitor
{
    public static UsageSnapshot Compute(IReadOnlyList<DesktopInstance> instances, IReadOnlyList<TerminalSession> terminals, Profile[] profiles)
    {
        var procs = ProcessSnapshot.Take();
        var children = new Dictionary<int, List<int>>();
        foreach (var p in procs.Values)
        {
            // A child must be newer than its parent (protects against reused parent pids).
            if (procs.TryGetValue(p.ParentPid, out var parent) && p.CreateTime >= parent.CreateTime && p.Pid != p.ParentPid)
            {
                if (!children.TryGetValue(p.ParentPid, out var l)) children[p.ParentPid] = l = new List<int>();
                l.Add(p.Pid);
            }
        }

        long TreeBytes(int root)
        {
            if (!procs.ContainsKey(root)) return -1;
            long sum = 0;
            var stack = new Stack<int>();
            var seen = new HashSet<int>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!seen.Add(id) || !procs.TryGetValue(id, out var info)) continue;
                sum += info.PrivateWorkingSet;
                if (children.TryGetValue(id, out var kids)) foreach (var k in kids) stack.Push(k);
            }
            return sum;
        }

        var map = new Dictionary<string, ProfileUsage>();
        ProfileUsage Get(string id) => map.TryGetValue(id, out var u) ? u : new ProfileUsage(id, false, 0, 0, 0);

        foreach (var inst in instances)
        {
            if (DesktopWindows.ProfileFor(inst, profiles) is not { } p) continue;
            var bytes = TreeBytes(inst.Pid);
            if (bytes < 0) continue;
            var u = Get(p.Id);
            map[p.Id] = u with { DesktopRunning = true, DesktopBytes = u.DesktopBytes + bytes };
        }
        foreach (var t in terminals)
        {
            if (!profiles.Any(p => p.Id == t.ProfileId)) continue;
            var bytes = TreeBytes(t.CmdPid);
            if (bytes < 0) continue;
            var u = Get(t.ProfileId);
            map[t.ProfileId] = u with { Terminals = u.Terminals + 1, TerminalBytes = u.TerminalBytes + bytes };
        }
        return new UsageSnapshot(map, map.Values.Sum(u => u.TotalBytes), DateTime.Now);
    }

    public static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    public static string Describe(ProfileUsage u)
    {
        var parts = new List<string>();
        if (u.DesktopRunning) parts.Add($"Desktop {Format(u.DesktopBytes)}");
        if (u.Terminals > 0) parts.Add($"{(u.Terminals == 1 ? "Terminal" : $"{u.Terminals} terminals")} {Format(u.TerminalBytes)}");
        return "● " + string.Join(" · ", parts);
    }
}
