using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeManager.Core;

/// <summary>
/// Sets the hover text of each Claude instance's own tray icon to "Claude (Profile)".
/// Uses the standard Shell_NotifyIcon(NIM_MODIFY, NIF_TIP) call; only the tooltip changes.
/// Claude's icon, menu, files and behavior are untouched. Restored to "Claude" on exit or when disabled.
/// </summary>
public static class TrayTips
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState; public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);

    const uint NIM_MODIFY = 1, NIF_TIP = 0x04, NIF_SHOWTIP = 0x80;
    const uint MaxProbeId = 40;

    static readonly object Gate = new();
    static readonly Dictionary<IntPtr, List<uint>> knownIcons = new(); // tray host window -> icon ids

    static bool SetTip(IntPtr host, uint id, string tip)
    {
        var d = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = host, uID = id, uFlags = NIF_TIP | NIF_SHOWTIP, szTip = tip };
        return Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    static List<IntPtr> HostsOf(int pid)
    {
        var hosts = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid)
            {
                var cls = new StringBuilder(128);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString().Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase)) hosts.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return hosts;
    }

    /// <summary>Applies the tooltip to every tray icon of the instance. Icon ids are discovered once, then cached.</summary>
    public static void Apply(int pid, string tip)
    {
        lock (Gate)
        {
            foreach (var dead in knownIcons.Keys.Where(h => !IsWindow(h)).ToList()) knownIcons.Remove(dead);
            foreach (var host in HostsOf(pid))
            {
                if (knownIcons.TryGetValue(host, out var ids))
                {
                    if (ids.All(id => SetTip(host, id, tip))) continue;
                    knownIcons.Remove(host); // icon re-created: discover again
                }
                // NIM_MODIFY on an id that doesn't exist just fails: probing is side-effect free.
                var found = new List<uint>();
                for (uint id = 0; id <= MaxProbeId; id++)
                    if (SetTip(host, id, tip)) found.Add(id);
                if (found.Count > 0) knownIcons[host] = found;
            }
        }
    }

    /// <summary>Puts every tooltip changed by Claude Manager back to "Claude".</summary>
    public static void RestoreAll()
    {
        lock (Gate)
        {
            foreach (var (host, ids) in knownIcons)
                if (IsWindow(host))
                    foreach (var id in ids) SetTip(host, id, "Claude");
            knownIcons.Clear();
        }
    }
}
