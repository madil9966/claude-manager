# Live soak: samples the running Claude Manager every N seconds:
# memory, handles, GDI/USER objects, and UI-thread response time (WM_NULL with 1 s timeout).
param([int]$Samples = 12, [int]$Interval = 15)
Add-Type @'
using System; using System.Runtime.InteropServices;
public class U {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32")] public static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
  [DllImport("user32")] public static extern int GetGuiResources(IntPtr h, int f);
}
'@
$p = Get-Process ClaudeManager -ErrorAction Stop
$script:target = $p.Id; $script:hwnd = [IntPtr]::Zero
[void][U]::EnumWindows({ param($h, $l) $x = 0; [void][U]::GetWindowThreadProcessId($h, [ref]$x); if ($x -eq $script:target -and $script:hwnd -eq [IntPtr]::Zero) { $script:hwnd = $h }; $true }, [IntPtr]::Zero)
"Claude Manager pid $($p.Id)"
for ($i = 1; $i -le $Samples; $i++) {
  $p.Refresh()
  $sw = [Diagnostics.Stopwatch]::StartNew(); $r = [IntPtr]::Zero
  $ok = [U]::SendMessageTimeout($script:hwnd, 0, [IntPtr]::Zero, [IntPtr]::Zero, 2, 1000, [ref]$r) -ne [IntPtr]::Zero
  $sw.Stop()
  '{0:HH:mm:ss}  private={1,5:N1} MB  handles={2,4}  GDI={3,3}  USER={4,3}  threads={5,2}  UI={6}' -f (Get-Date), ($p.PrivateMemorySize64 / 1MB), $p.HandleCount,
    [U]::GetGuiResources($p.Handle, 0), [U]::GetGuiResources($p.Handle, 1), $p.Threads.Count, $(if ($ok) { "$($sw.ElapsedMilliseconds) ms" } else { 'NOT RESPONDING' })
  if ($i -lt $Samples) { Start-Sleep $Interval }
}
