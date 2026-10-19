# Generates assets/app.ico: the Claude Manager glyph (two overlapping circles = two accounts),
# the same design the tray icon uses (IconFactory.AppIcon). Run again only if the design changes.
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Render([int]$size) {
  $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
  $d = $size * 0.62
  $back = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#2F80ED'))
  $front = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#D97757'))
  $ring = New-Object Drawing.Pen ([Drawing.Color]::White), ([Math]::Max(1.5, $size / 14))
  $g.FillEllipse($back, $size - $d - 1, 1, $d, $d)
  $g.FillEllipse($front, 1, $size - $d - 1, $d, $d)
  $g.DrawEllipse($ring, 1, $size - $d - 1, $d, $d)
  $g.Dispose(); $back.Dispose(); $front.Dispose(); $ring.Dispose()
  $ms = New-Object IO.MemoryStream
  $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = $sizes | ForEach-Object { , (Render $_) }
$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$pngs[$i].Length); $w.Write([int]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"Wrote $out ($((Get-Item $out).Length) bytes, sizes $($sizes -join ', '))"

# PNGs for macOS: app icon (window/about), menu bar icon, and a 1024 px source for the .icns (built on macOS).
foreach ($pair in @(@(256, 'app-256.png'), @(1024, 'app-1024.png'), @(44, 'tray-mac.png'))) {
  $file = Join-Path (Split-Path $out) $pair[1]
  [IO.File]::WriteAllBytes($file, (Render $pair[0]))
  "Wrote $file"
}
