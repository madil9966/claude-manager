# Diagnostic: list top-level windows of Claude desktop processes.
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public class W {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32")] public static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
'@
$main = Get-CimInstance Win32_Process -Filter "Name='claude.exe'" | Where-Object { $_.ExecutablePath -match 'AnthropicClaude' -and $_.CommandLine -notmatch '--type=' }
foreach ($m in $main) {
  $dir = if ($m.CommandLine -match '--user-data-dir="?([^"]+)"?') { $Matches[1] } else { '(default %APPDATA%\Claude)' }
  "PID $($m.ProcessId)  data=$dir"
  $script:pidWanted = [int]$m.ProcessId
  $cb = [W+P]{ param($h, $l)
    $p = 0; [void][W]::GetWindowThreadProcessId($h, [ref]$p)
    if ($p -eq $script:pidWanted) {
      $sb = New-Object Text.StringBuilder 256; [void][W]::GetWindowText($h, $sb, 256)
      if ($sb.Length -gt 0) { $script:found.Add("    window visible=$([W]::IsWindowVisible($h)) minimized=$([W]::IsIconic($h)) title='$sb'") }
    }
    $true }
  $script:found = New-Object System.Collections.Generic.List[string]
  [void][W]::EnumWindows($cb, [IntPtr]::Zero)
  $script:found
}
