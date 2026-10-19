# Builds the distributable files into .\dist:
#   ClaudeManager-Setup-<version>.exe   installer (wizard; per-user, no admin)
#   ClaudeManager-<version>-portable.zip
#   SHA256SUMS.txt
#
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 [-SelfContained]
#
# Default: framework-dependent (small; needs the .NET 9 Desktop Runtime, which Windows offers to install).
# -SelfContained: bundles the runtime (~ 60-70 MB each, nothing else to install).
param([switch]$SelfContained, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

[xml]$props = Get-Content 'src\Directory.Build.props'
$version = $props.Project.PropertyGroup.Version
$payload = Join-Path $root 'artifacts\payload'
$dist = Join-Path $root 'dist'
$sc = if ($SelfContained) { 'true' } else { 'false' }
$common = @('-c', 'Release', '-r', 'win-x64', "--self-contained", $sc, '-p:PublishSingleFile=true', '-p:DebugType=none', '-p:DebugSymbols=false')
if ($SelfContained) { $common += '-p:EnableCompressionInSingleFile=true' }

Write-Host "== Claude Manager $version ($(if ($SelfContained) {'self-contained'} else {'framework-dependent'}))" -ForegroundColor Cyan
Remove-Item $payload, $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $payload, $dist | Out-Null

Write-Host '== Publishing app'
dotnet publish src\ClaudeManager\ClaudeManager.csproj @common -o $payload | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }
Copy-Item LICENSE (Join-Path $payload 'LICENSE.txt')
Copy-Item README.md $payload

if (-not $SkipTests) {
  Write-Host '== Self-test of the published app'
  $out = & (Join-Path $payload 'ClaudeManager.exe') --selftest | Out-String
  if ($out -notmatch 'ALL PASSED') { $out | Out-Host; throw 'Self-test failed' }
  Write-Host 'ALL PASSED'
}

Write-Host '== Building installer'
dotnet publish src\ClaudeManager.Setup\ClaudeManager.Setup.csproj @common "-p:PayloadDir=$payload\" -p:RequirePayload=true -o (Join-Path $root 'artifacts\setup') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed' }

$suffix = if ($SelfContained) { '-selfcontained' } else { '' }
$setup = Join-Path $dist "ClaudeManager-Setup-$version$suffix.exe"
Copy-Item (Join-Path $root 'artifacts\setup\ClaudeManager-Setup.exe') $setup
$zip = Join-Path $dist "ClaudeManager-$version$suffix-portable.zip"
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip

Get-ChildItem $dist -File | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } |
  Set-Content (Join-Path $dist 'SHA256SUMS.txt')
Write-Host '== Done' -ForegroundColor Green
Get-ChildItem $dist | ForEach-Object { '{0,-48} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
