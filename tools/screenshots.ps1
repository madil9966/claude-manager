# Takes the README / website screenshots with SAMPLE data (demo mode: no real profiles, nothing saved)
# into docs\screenshots. Stops a running Claude Manager first and restarts it afterwards.
#   powershell -ExecutionPolicy Bypass -File tools\screenshots.ps1
param([string]$Exe = "$PSScriptRoot\..\src\ClaudeManager\bin\Release\net9.0-windows\ClaudeManager.exe",
      [string]$Setup = (Get-ChildItem "$PSScriptRoot\..\dist\ClaudeManager-Setup-*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1).FullName)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public class Shots {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32")] public static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h);
  public struct RECT { public int L, T, R, B; }
}
'@
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\screenshots'; New-Item -ItemType Directory -Force $out | Out-Null
$UIA = [Windows.Automation.AutomationElement]
$exe = (Resolve-Path $Exe).Path

function Find-Window([int]$procId, [string]$prefix) {
  $script:hit = [IntPtr]::Zero
  [void][Shots]::EnumWindows({ param($h, $l)
    $x = 0; [void][Shots]::GetWindowThreadProcessId($h, [ref]$x)
    if ($x -eq $procId -and [Shots]::IsWindowVisible($h)) {
      $sb = New-Object Text.StringBuilder 120; [void][Shots]::GetWindowText($h, $sb, 120)
      if ($sb.ToString().StartsWith($prefix)) { $script:hit = $h }
    }
    $true }, [IntPtr]::Zero)
  $script:hit
}
function Save-Window([IntPtr]$h, [string]$name) {
  Start-Sleep -Milliseconds 500
  $r = New-Object Shots+RECT; [void][Shots]::GetWindowRect($h, [ref]$r)
  $bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc(); [void][Shots]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
  $file = Join-Path $out "$name.png"; $bmp.Save($file, [Drawing.Imaging.ImageFormat]::Png)
  "  {0,-34} {1}x{2}" -f "$name.png", $bmp.Width, $bmp.Height; $bmp.Dispose()
}
function Panel-Element($procId) {
  $UIA::RootElement.FindAll('Children', (New-Object Windows.Automation.PropertyCondition $UIA::ProcessIdProperty, $procId)) |
    Where-Object { $_.Current.Name -eq 'Claude Manager' } | Select-Object -First 1
}

Get-Process ClaudeManager -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 800
$env:CLAUDE_MANAGER_DEMO = '1'
foreach ($theme in 'light', 'dark') {
  Get-Process ClaudeManager -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 600
  $env:CLAUDE_MANAGER_THEME = $theme
  $p = Start-Process $exe -ArgumentList '--tray' -PassThru
  Start-Sleep 4                                   # detection + Claude icon
  $panel = $null
  for ($try = 0; $try -lt 4 -and -not $panel; $try++) { Start-Process $exe | Out-Null; Start-Sleep 1.2; $panel = Panel-Element $p.Id }
  if (-not $panel) { "[$theme] panel not found"; continue }
  "[$theme]"
  Save-Window ([IntPtr]$panel.Current.NativeWindowHandle) "windows-panel-$theme"

  # Edit dialog of the sample "Work" profile (⋯ → Edit…).
  $h = [IntPtr]::Zero
  for ($try = 0; $try -lt 3 -and $h -eq [IntPtr]::Zero; $try++) {   # menu keyboard timing can miss once
    $panel = Panel-Element $p.Id
    if (-not $panel) { Start-Process $exe | Out-Null; Start-Sleep 1.2; continue }
    $more = $panel.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition $UIA::NameProperty, 'More actions for Work'))
    if (-not $more) { Start-Sleep 1; continue }
    [void][Shots]::SetForegroundWindow([IntPtr]$panel.Current.NativeWindowHandle)
    [void][Shots]::PostMessage([IntPtr]$more.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 900
    [System.Windows.Forms.SendKeys]::SendWait('{DOWN}'); Start-Sleep -Milliseconds 250; [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep 1.5
    $h = Find-Window $p.Id 'Edit profile'
    if ($h -eq [IntPtr]::Zero) { [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 400 }
  }
  if ($h -ne [IntPtr]::Zero) {
    Save-Window $h "windows-edit-profile-$theme"
    [void][Shots]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # close = Cancel
  } else { '  edit dialog did not open' }
}

# Installer: welcome + license pages (the options page shows the real user folder, so it's skipped).
Remove-Item Env:CLAUDE_MANAGER_THEME
if ($Setup) {
  '[installer]'
  $sp = Start-Process $Setup -PassThru; Start-Sleep 2.5; $sp.Refresh()
  $sh = [IntPtr]$sp.MainWindowHandle
  Save-Window $sh 'windows-installer-welcome'
  $win = $UIA::FromHandle($sh)
  $next = $win.FindFirst('Descendants', (New-Object Windows.Automation.PropertyCondition $UIA::NameProperty, 'Next >'))
  [void][Shots]::PostMessage([IntPtr]$next.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
  Start-Sleep 1
  Save-Window $sh 'windows-installer-license'
  Stop-Process -Id $sp.Id -Force
}

Get-Process ClaudeManager -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500
Remove-Item Env:CLAUDE_MANAGER_DEMO
Start-Process $exe -ArgumentList '--tray' | Out-Null
'restarted Claude Manager normally'
