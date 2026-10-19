# Generates docs/social-preview.png (1280x640): the image shown when the site or repo is shared.
# Upload the same file in GitHub: Settings -> General -> Social preview.
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'docs\social-preview.png'
$W = 1280; $H = 640
$bmp = New-Object Drawing.Bitmap $W, $H
$g = [Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'

$bg = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point $W, $H), ([Drawing.Color]::FromArgb(20, 22, 30)), ([Drawing.Color]::FromArgb(32, 38, 58))
$g.FillRectangle($bg, 0, 0, $W, $H)

# Soft glow circles (the two-accounts motif)
$blue = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(60, 47, 128, 237))
$orange = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(55, 217, 119, 87))
$g.FillEllipse($blue, 830, -120, 560, 560)
$g.FillEllipse($orange, 700, 260, 520, 520)

$icon = [Drawing.Image]::FromFile((Join-Path $root 'assets\app-256.png'))
$g.DrawImage($icon, 80, 92, 128, 128)

$fam = if ((New-Object Drawing.Text.InstalledFontCollection).Families.Name -contains 'Segoe UI Variable Display') { 'Segoe UI Variable Display' } else { 'Segoe UI' }
$white = [Drawing.Brushes]::White
$muted = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(200, 205, 215))
$g.DrawString('Claude Manager', (New-Object Drawing.Font $fam, 64, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)), $white, 230, 112)
$g.DrawString('Run multiple Claude accounts', (New-Object Drawing.Font $fam, 54, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)), $white, 80, 268)
$g.DrawString('at the same time', (New-Object Drawing.Font $fam, 54, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)), $white, 80, 334)
$g.DrawString('Work + Personal side by side in the Claude desktop app and Claude Code', (New-Object Drawing.Font $fam, 26, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)), $muted, 82, 420)

# Platform pills
$pillFont = New-Object Drawing.Font $fam, 22, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
$x = 82
foreach ($label in 'Windows', 'macOS', 'Free & open source') {
  $sz = $g.MeasureString($label, $pillFont)
  $r = New-Object Drawing.RectangleF $x, 484, ($sz.Width + 32), 44
  $path = New-Object Drawing.Drawing2D.GraphicsPath
  $d = 44; $path.AddArc($r.X, $r.Y, $d, $d, 90, 180); $path.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 180); $path.CloseFigure()
  $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(45, 255, 255, 255))), $path)
  $g.DrawString($label, $pillFont, $white, $x + 16, 492)
  $x += $r.Width + 14
}
$g.DrawString('Unofficial community tool · not affiliated with Anthropic · github.com/madil9966/claude-manager', (New-Object Drawing.Font $fam, 18, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)), $muted, 82, 578)

$g.Dispose(); $icon.Dispose()
$bmp.Save($out, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Copy-Item (Join-Path $root 'assets\app-256.png') (Join-Path $root 'docs\app-256.png') -Force
"Wrote $out and docs\app-256.png"
