# Opens the Add-profile and Edit-profile dialogs (light and dark), checks layout with UI Automation,
# triggers a validation error, and saves screenshots to artifacts\screens\dialog-*.png. Nothing is saved:
# every dialog is cancelled. Leaves Claude Manager running in the system theme.
param([string]$Exe = "$PSScriptRoot\..\src\ClaudeManager\bin\Release\net9.0-windows\ClaudeManager.exe")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public class D {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public struct RECT { public int L, T, R, B; }
}
'@
$out = Join-Path $PSScriptRoot '..\artifacts\screens'; New-Item -ItemType Directory -Force $out | Out-Null
$UIA = [Windows.Automation.AutomationElement]; $Scope = [Windows.Automation.TreeScope]; $CT = [Windows.Automation.ControlType]
$exe = (Resolve-Path $Exe).Path

function Cond($prop, $val) { New-Object Windows.Automation.PropertyCondition $prop, $val }
function TopWindow($procId, $name, [int]$tries = 25) {
  # Dialogs owned by the panel appear under it in the UIA tree, so search descendants, not just top-level.
  # Only this app's top-level windows and what they own (searching the whole desktop tree is slow).
  for ($i = 0; $i -lt $tries; $i++) {
    try {
      foreach ($top in $UIA::RootElement.FindAll($Scope::Children, (Cond $UIA::ProcessIdProperty $procId))) {
        if ($top.Current.Name -like $name) { return $top }
        foreach ($w in $top.FindAll($Scope::Children, (Cond $UIA::ControlTypeProperty $CT::Window))) { if ($w.Current.Name -like $name) { return $w } }
      }
    } catch { }
    Start-Sleep -Milliseconds 200
  }
  $null
}
function PostClick($el) { [void][D]::PostMessage([IntPtr]$el.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) }
function Snap($win, $name) {
  Start-Sleep -Milliseconds 350
  $h = [IntPtr]$win.Current.NativeWindowHandle; $r = New-Object D+RECT; [void][D]::GetWindowRect($h, [ref]$r)
  $bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc(); [void][D]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
  $file = Join-Path $out "$name.png"; $bmp.Save($file); $bmp.Dispose(); "    screenshot $name.png ($($r.R - $r.L)x$($r.B - $r.T))"
}
function CheckLayout($win) {
  $wr = $win.Current.BoundingRectangle
  $all = $win.FindAll($Scope::Children, [Windows.Automation.Condition]::TrueCondition)
  $rects = @(); $outside = @()
  foreach ($c in $all) {
    $br = $c.Current.BoundingRectangle
    if ($br.IsEmpty -or $c.Current.IsOffscreen) { continue }
    $label = "$($c.Current.ControlType.ProgrammaticName.Replace('ControlType.',''))'$($c.Current.Name)'"
    if ($br.Left -lt $wr.Left -or $br.Right -gt $wr.Right + 1 -or $br.Bottom -gt $wr.Bottom + 1) { $outside += $label }
    $rects += , @($label, $br)
  }
  $overlaps = @()
  for ($i = 0; $i -lt $rects.Count; $i++) { for ($j = $i + 1; $j -lt $rects.Count; $j++) {
    $x = [Windows.Rect]::Intersect($rects[$i][1], $rects[$j][1])
    if (-not $x.IsEmpty -and $x.Width -gt 2 -and $x.Height -gt 2) { $overlaps += "$($rects[$i][0]) / $($rects[$j][0])" }
  } }
  "    controls: $($rects.Count)  outside: $(if ($outside) { $outside -join ', ' } else { 'none' })  overlapping: $(if ($overlaps) { $overlaps -join '; ' } else { 'none' })"
}

foreach ($theme in 'light', 'dark') {
  Get-Process ClaudeManager -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 600
  $env:CLAUDE_MANAGER_THEME = $theme
  $p = Start-Process $exe -ArgumentList '--tray' -PassThru
  Start-Sleep 3
  $panel = $null
  for ($try = 0; $try -lt 4 -and -not $panel; $try++) { Start-Process $exe | Out-Null; Start-Sleep -Milliseconds 900; $panel = TopWindow $p.Id 'Claude Manager' }
  if (-not $panel) { "[$theme] panel not found"; continue }
  "[$theme]"

  # ---- Add profile ----
  PostClick ($panel.FindFirst($Scope::Descendants, (Cond $UIA::NameProperty 'Add profile')))
  $dlg = TopWindow $p.Id 'Add profile'
  if (-not $dlg) { '  Add dialog did not open'; continue }
  '  Add profile dialog'
  CheckLayout $dlg
  Snap $dlg "dialog-add-$theme"
  $edits = $dlg.FindAll($Scope::Descendants, (Cond $UIA::ControlTypeProperty $CT::Edit))
  "    edit fields: $($edits.Count)"
  $edits[0].GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Client A')
  Start-Sleep -Milliseconds 300
  $cliSuggest = $edits[2].GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
  "    folder suggestion follows name: $($cliSuggest -match 'client-a')  ($cliSuggest)"
  Snap $dlg "dialog-add-named-$theme"
  $edits[1].GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('relative\folder')
  PostClick ($dlg.FindFirst($Scope::Descendants, (Cond $UIA::NameProperty 'Create profile')))
  Start-Sleep -Milliseconds 500
  $alert = $dlg.FindFirst($Scope::Descendants, (Cond $UIA::ControlTypeProperty $CT::Text)) # banner is custom: check by name below
  $banner = $dlg.FindAll($Scope::Descendants, [Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -match 'must be a full path' } | Select-Object -First 1
  "    validation error shown: $([bool]$banner)"
  CheckLayout $dlg
  Snap $dlg "dialog-add-error-$theme"
  PostClick ($dlg.FindFirst($Scope::Descendants, (Cond $UIA::NameProperty 'Cancel')))
  Start-Sleep -Milliseconds 500
  "    closed by Cancel: $(-not (TopWindow $p.Id 'Add profile' 3))"

  # ---- Edit (Default profile: read-only folders) ----
  $panel = $null
  for ($try = 0; $try -lt 4 -and -not $panel; $try++) { Start-Process $exe | Out-Null; Start-Sleep -Milliseconds 900; $panel = TopWindow $p.Id 'Claude Manager' }
  $more = $panel.FindFirst($Scope::Descendants, (Cond $UIA::NameProperty 'More actions for Default'))
  # Open the menu, then pick "Edit…" with the keyboard like a user would. (Invoking the menu item through
  # UI Automation would run the modal dialog inside that UIA call and block further UIA queries.)
  [void][D]::SetForegroundWindow([IntPtr]$panel.Current.NativeWindowHandle)
  PostClick $more
  Start-Sleep -Milliseconds 700
  [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
  Start-Sleep -Milliseconds 150
  [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
  $edit = TopWindow $p.Id 'Edit profile*'
  if ($edit) {
    '  Edit profile dialog (Default)'
    CheckLayout $edit
    $ro = $edit.FindAll($Scope::Descendants, (Cond $UIA::ControlTypeProperty $CT::Edit)) | Where-Object { $_.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.IsReadOnly }
    "    read-only folder fields: $(@($ro).Count)"
    Snap $edit "dialog-edit-default-$theme"
    PostClick ($edit.FindFirst($Scope::Descendants, (Cond $UIA::NameProperty 'Cancel')))
  } else { '  Edit dialog did not open (menu keyboard navigation)' }
}
Get-Process ClaudeManager -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500
Remove-Item Env:CLAUDE_MANAGER_THEME
Start-Process $exe -ArgumentList '--tray' | Out-Null
'restarted Claude Manager in the system theme'
