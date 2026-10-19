# Opens the tray panel in light and dark theme, checks its layout with UI Automation (every control
# inside the window, buttons not overlapping) and saves screenshots to artifacts\screens\panel-*.png.
# Stops any running Claude Manager first and leaves the app running in the normal (system) theme.
param([string]$Exe = "$PSScriptRoot\..\src\ClaudeManager\bin\Release\net9.0-windows\ClaudeManager.exe")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class P {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public struct RECT { public int L, T, R, B; }
}
'@
$out = Join-Path $PSScriptRoot '..\artifacts\screens'; New-Item -ItemType Directory -Force $out | Out-Null
$A = [Windows.Automation.AutomationElement]; $T = [Windows.Automation.TreeScope]
$exe = (Resolve-Path $Exe).Path

function Stop-App { Get-Process ClaudeManager -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 600 }

foreach ($theme in 'light', 'dark') {
  Stop-App
  $env:CLAUDE_MANAGER_THEME = $theme
  $p = Start-Process $exe -ArgumentList '--tray' -PassThru
  Start-Sleep 3
  # The panel auto-hides when another app takes the foreground (this console may), so open and look a few times.
  $h = [IntPtr]::Zero
  for ($try = 0; $try -lt 4 -and $h -eq [IntPtr]::Zero; $try++) {
    Start-Process $exe | Out-Null          # second launch -> IPC -> opens the panel
    Start-Sleep -Milliseconds 900
    foreach ($w in $A::RootElement.FindAll($T::Children, (New-Object Windows.Automation.PropertyCondition $A::ProcessIdProperty, $p.Id))) {
      if ($w.Current.Name -eq 'Claude Manager') { $h = [IntPtr]$w.Current.NativeWindowHandle; $win = $w }
    }
  }
  if ($h -eq [IntPtr]::Zero) { "[$theme] panel window not found"; continue }
  $r = New-Object P+RECT; [void][P]::GetWindowRect($h, [ref]$r)
  "[$theme] panel {0}x{1} at {2},{3}" -f ($r.R - $r.L), ($r.B - $r.T), $r.L, $r.T

  # Layout checks.
  $wr = $win.Current.BoundingRectangle
  $buttons = $win.FindAll($T::Descendants, (New-Object Windows.Automation.PropertyCondition $A::ControlTypeProperty, ([Windows.Automation.ControlType]::Button)))
  $outside = @(); $rects = @()
  foreach ($b in $buttons) {
    $br = $b.Current.BoundingRectangle
    if ($br.IsEmpty) { continue }
    if ($br.Left -lt $wr.Left -or $br.Right -gt $wr.Right + 1 -or $br.Top -lt $wr.Top -or $br.Bottom -gt $wr.Bottom + 1) { $outside += $b.Current.Name }
    $rects += , @($b.Current.Name, $br)
  }
  $overlaps = @()
  for ($i = 0; $i -lt $rects.Count; $i++) { for ($j = $i + 1; $j -lt $rects.Count; $j++) {
    $r1 = $rects[$i][1]; $r2 = $rects[$j][1]   # (PowerShell names are case-insensitive: don't reuse $A)
    $x = [Windows.Rect]::Intersect($r1, $r2)
    if (-not $x.IsEmpty -and $x.Width -gt 1 -and $x.Height -gt 1) { $overlaps += "$($rects[$i][0]) / $($rects[$j][0])" }
  } }
  "  buttons: $($buttons.Count)  ({0})" -f (($rects | ForEach-Object { $_[0] }) -join ', ')
  "  outside window: " + $(if ($outside) { $outside -join ', ' } else { 'none' })
  "  overlapping: " + $(if ($overlaps) { $overlaps -join '; ' } else { 'none' })

  $bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc(); [void][P]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
  $file = Join-Path $out "panel-$theme.png"; $bmp.Save($file)
  $bg = $bmp.GetPixel([int]($bmp.Width / 2), 6); "  background sample: $($bg.R),$($bg.G),$($bg.B)  -> $file"
  $bmp.Dispose()
}
Stop-App
Remove-Item Env:CLAUDE_MANAGER_THEME
Start-Process $exe -ArgumentList '--tray' | Out-Null
'restarted Claude Manager in the system theme'
