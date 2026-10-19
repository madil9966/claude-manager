# Diagnostic: memory/handles of Claude Manager and of each Claude desktop instance (whole process tree).
$all = Get-CimInstance Win32_Process
function Tree($procId) { $kids = $all | Where-Object { $_.ParentProcessId -eq $procId }; @($procId) + @($kids | ForEach-Object { Tree $_.ProcessId }) }
function Report($label, $ids) {
  $ps = $ids | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }
  $ws = ($ps | Measure-Object WorkingSet64 -Sum).Sum / 1MB
  $pv = ($ps | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB
  $h  = ($ps | Measure-Object HandleCount -Sum).Sum
  '{0,-34} procs={1,3}  working={2,7:N0} MB  private={3,7:N0} MB  handles={4,6}' -f $label, @($ps).Count, $ws, $pv, $h
}
$cm = Get-Process ClaudeManager -ErrorAction SilentlyContinue
if ($cm) { Report "Claude Manager (pid $($cm.Id))" @($cm.Id); 'Claude Manager threads={0} GDI/USER see Task Manager' -f $cm.Threads.Count }
$main = $all | Where-Object { $_.Name -eq 'claude.exe' -and $_.ExecutablePath -match 'AnthropicClaude' -and $_.CommandLine -notmatch '--type=' }
foreach ($m in $main) {
  $dir = if ($m.CommandLine -match '--user-data-dir="?([^"]+)"?') { Split-Path $Matches[1] -Leaf } else { 'Default' }
  Report "Claude desktop [$dir] (pid $($m.ProcessId))" (Tree $m.ProcessId)
}
