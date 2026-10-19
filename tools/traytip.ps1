# Feasibility probe: find each Claude instance's tray-icon host window and try to set its tooltip.
param([switch]$Apply)
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public class T {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32")] public static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct NID { public int cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string szTip; public uint dwState; public uint dwStateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string szInfo; public uint uVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string szInfoTitle; public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon; }
  [DllImport("shell32", CharSet=CharSet.Unicode)] public static extern bool Shell_NotifyIcon(uint msg, ref NID d);
  public static bool SetTip(IntPtr h, uint id, string tip) { var d = new NID(); d.cbSize = Marshal.SizeOf(typeof(NID)); d.hWnd = h; d.uID = id; d.uFlags = 0x4 | 0x80; d.szTip = tip; return Shell_NotifyIcon(1, ref d); }
}
'@
$main = Get-CimInstance Win32_Process -Filter "Name='claude.exe'" | Where-Object { $_.ExecutablePath -match 'AnthropicClaude' -and $_.CommandLine -notmatch '--type=' }
foreach ($m in $main) {
  $name = if ($m.CommandLine -match '--user-data-dir="?([^"]+)"?') { (Split-Path $Matches[1] -Leaf) -replace '^Claude-','' } else { 'Default' }
  $script:want = [int]$m.ProcessId; $script:hosts = New-Object System.Collections.Generic.List[IntPtr]
  [void][T]::EnumWindows({ param($h,$l) $p=0; [void][T]::GetWindowThreadProcessId($h,[ref]$p); if ($p -eq $script:want) { $sb = New-Object Text.StringBuilder 128; [void][T]::GetClassName($h,$sb,128); if ($sb.ToString() -like '*NotifyIcon*') { $script:hosts.Add($h) } }; $true }, [IntPtr]::Zero)
  "PID $($m.ProcessId) [$name] tray host windows: $($script:hosts.Count)"
  foreach ($h in $script:hosts) {
    if ($Apply) { foreach ($id in 0..40) { if ([T]::SetTip($h, [uint32]$id, "Claude ($name)")) { "   modified uID=$id -> 'Claude ($name)'" } } }
  }
}
