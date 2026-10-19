# Walks the installer wizard with UI Automation (Welcome -> License -> Options), saves a screenshot of
# each page to artifacts\screens, then cancels. Nothing is installed.
# -Install: really installs through the wizard (default folder, no launch) and shows the Finish page.
param([string]$Setup = (Get-ChildItem "$PSScriptRoot\..\dist\ClaudeManager-Setup-*.exe" | Select-Object -First 1).FullName,
      [switch]$Install)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class Shot {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
  /// <summary>Visible standard dialog (#32770, e.g. a MessageBox) of the process, or zero.</summary>
  public static IntPtr DialogOf(int pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      int p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) {
        var cn = new System.Text.StringBuilder(64); GetClassName(h, cn, 64);
        if (cn.ToString() == "#32770") { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public struct RECT { public int L, T, R, B; }
}
'@
$out = Join-Path $PSScriptRoot '..\artifacts\screens'
New-Item -ItemType Directory -Force $out | Out-Null
$A = [Windows.Automation.AutomationElement]; $T = [Windows.Automation.TreeScope]

$proc = Start-Process $Setup -PassThru
$win = $null
for ($i = 0; $i -lt 40 -and -not $win; $i++) {
  Start-Sleep -Milliseconds 250
  $proc.Refresh()
  if ($proc.MainWindowHandle -ne 0) { $win = $A::FromHandle($proc.MainWindowHandle) }
}
if (-not $win) { throw 'Setup window did not appear' }
"window: $($win.Current.Name)"

function Snap($name) {
  Start-Sleep -Milliseconds 400
  $h = [IntPtr]$proc.MainWindowHandle; $r = New-Object Shot+RECT; [void][Shot]::GetWindowRect($h, [ref]$r)
  $bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc(); [void][Shot]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
  $file = Join-Path $out "$name.png"; $bmp.Save($file); $bmp.Dispose(); "  saved $file"
}
function Find($name, $type) {
  $c = New-Object Windows.Automation.AndCondition (New-Object Windows.Automation.PropertyCondition $A::NameProperty, $name),
       (New-Object Windows.Automation.PropertyCondition $A::ControlTypeProperty, $type)
  $win.FindFirst($T::Descendants, $c)
}
function Click($name) {
  $b = Find $name ([Windows.Automation.ControlType]::Button)
  if (-not $b) { throw "button '$name' not found" }
  if (-not $b.Current.IsEnabled) { throw "button '$name' is disabled" }
  $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Texts { ($win.FindAll($T::Descendants, (New-Object Windows.Automation.PropertyCondition $A::ControlTypeProperty, ([Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name }) -join ' | ' }

"1 Welcome: " + ((Texts) -match 'not affiliated' | ForEach-Object { 'disclaimer shown' })
Snap '1-welcome'
Click 'Next >'
$next = Find 'Next >' ([Windows.Automation.ControlType]::Button)
"2 License: Next enabled before accepting = $($next.Current.IsEnabled)"
Snap '2-license'
$cb = $win.FindFirst($T::Descendants, (New-Object Windows.Automation.PropertyCondition $A::ControlTypeProperty, ([Windows.Automation.ControlType]::CheckBox)))
$cb.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
Start-Sleep -Milliseconds 200
"   after accepting = $($next.Current.IsEnabled)"
Click 'Next >'
$edit = $win.FindFirst($T::Descendants, (New-Object Windows.Automation.PropertyCondition $A::ControlTypeProperty, ([Windows.Automation.ControlType]::Edit)))
"3 Options: folder = $($edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value)"
$installBtn = Find 'Install' ([Windows.Automation.ControlType]::Button)
"   primary button = $(if ($installBtn) {'Install'} else {'(missing)'})"
Snap '3-options'
# Invalid folder is rejected without installing:
$edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('C:\Windows\ClaudeManager')
Click 'Install'
Start-Sleep -Milliseconds 300
"   C:\Windows rejected: " + [bool]((Texts) -match 'administrator rights')
Snap '3b-options-error'

if ($Install) {
  $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue((Join-Path $env:LOCALAPPDATA 'Programs\Claude Manager'))
  Click 'Install'
  $finish = $null
  for ($i = 0; $i -lt 60 -and -not $finish; $i++) { Start-Sleep -Milliseconds 250; $finish = Find 'Finish' ([Windows.Automation.ControlType]::Button) }
  "4 Finished page: $([bool]$finish)  | " + ((Texts) -split ' \| ' | Where-Object { $_ -match 'installed|failed' } | Select-Object -First 1)
  Snap '4-finished'
  $launch = $win.FindFirst($T::Descendants, (New-Object Windows.Automation.PropertyCondition $A::NameProperty, 'Open Claude Manager now'))
  if ($launch -and $launch.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On') {
    $launch.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
  }
  Click 'Finish'
  $proc.WaitForExit(5000) | Out-Null
  "closed after finish: $($proc.HasExited)"
  "installed: " + (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs\Claude Manager\ClaudeManager.exe'))
  return
}

# Cancel opens a modal confirmation, and UIA Invoke would block until it closes, so post the click instead.
$cancelBtn = Find 'Cancel' ([Windows.Automation.ControlType]::Button)
[void][Shot]::PostMessage([IntPtr]$cancelBtn.Current.NativeWindowHandle, 0x00F5 <# BM_CLICK #>, [IntPtr]::Zero, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500
# Confirm the "Cancel the installation?" message box.
$yes = $null
for ($i = 0; $i -lt 20 -and -not $yes; $i++) {
  Start-Sleep -Milliseconds 200
  $dlgHandle = [Shot]::DialogOf($proc.Id)
  if ($dlgHandle -ne [IntPtr]::Zero) {
    $yes = $A::FromHandle($dlgHandle).FindFirst($T::Descendants, (New-Object Windows.Automation.PropertyCondition $A::NameProperty, 'Yes'))
  }
}
"cancel asks for confirmation: $([bool]$yes)"
if ($yes) { [void][Shot]::PostMessage([IntPtr]$yes.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) }
$proc.WaitForExit(5000) | Out-Null
"closed after cancel: $($proc.HasExited)"
"installed anything: " + (Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeManager')
