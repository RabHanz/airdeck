# Draws the Airdeck icon (a remote silhouette with an amber signal dot) into assets\airdeck.ico
# at 16-256 px, stored as PNG-compressed icon frames.
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'assets\airdeck.ico'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null

function Draw-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $size / 32.0
    # Rounded tile background.
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 7 * $s; $d = $r * 2; $w = $size - 1
    $tile.AddArc(0, 0, $d, $d, 180, 90); $tile.AddArc($w - $d, 0, $d, $d, 270, 90)
    $tile.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $tile.AddArc(0, $w - $d, $d, $d, 90, 90); $tile.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(0, $size)), ([System.Drawing.Color]::FromArgb(38, 39, 44)), ([System.Drawing.Color]::FromArgb(16, 17, 19))
    $g.FillPath($bg, $tile)
    # Remote body.
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bx = 10.5 * $s; $bw = 11 * $s; $by = 3.5 * $s; $bh = 25 * $s; $br = 5.5 * $s
    $body.AddArc($bx, $by, $br * 2, $br * 2, 180, 90); $body.AddArc($bx + $bw - $br * 2, $by, $br * 2, $br * 2, 270, 90)
    $body.AddArc($bx + $bw - $br * 2, $by + $bh - $br * 2, $br * 2, $br * 2, 0, 90); $body.AddArc($bx, $by + $bh - $br * 2, $br * 2, $br * 2, 90, 90); $body.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(236, 232, 224))), $body)
    # Amber signal dot with a soft halo.
    $cx = 16 * $s; $cy = 10 * $s
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 255, 178, 36))), $cx - 5.2 * $s, $cy - 5.2 * $s, 10.4 * $s, 10.4 * $s)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 178, 36))), $cx - 3.6 * $s, $cy - 3.6 * $s, 7.2 * $s, 7.2 * $s)
    # Two keys.
    $key = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(48, 50, 56))
    $g.FillRectangle($key, 13.5 * $s, 18 * $s, 5 * $s, 1.8 * $s)
    $g.FillRectangle($key, 13.5 * $s, 22 * $s, 5 * $s, 1.8 * $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

# Classic 32-bit DIB icon frame (BITMAPINFOHEADER + bottom-up BGRA + empty AND mask). Small sizes use
# this format because .NET's Icon class (tray icon) misreads PNG-compressed frames.
function Dib-Frame([byte[]]$png) {
    $bmp = New-Object System.Drawing.Bitmap (New-Object System.IO.MemoryStream (, $png))
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $bmp.LockBits($rect, 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($w * $h * 4)
    [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data); $bmp.Dispose()
    $maskRow = [int]([Math]::Ceiling($w / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]0); $bw.Write([uint32]($w * $h * 4 + $maskRow * $h)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $w * 4, $w * 4) }
    $bw.Write((New-Object byte[] ($maskRow * $h)))
    $bw.Flush()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = $sizes | ForEach-Object { $png = Draw-Frame $_; if ($_ -ge 256) { ,$png } else { ,(Dib-Frame $png) } }
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $px = $sizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($(if ($px -ge 256) { 0 } else { $px }))); $w.Write([byte]($(if ($px -ge 256) { 0 } else { $px })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
$w.Close()
Write-Host "Wrote $Out"

# PNG copies for the app window (favicon / web manifest), so Windows shows the Airdeck icon, not a globe.
$ui = Join-Path (Split-Path $PSScriptRoot -Parent) 'ui'
foreach ($px in 32, 192, 512) { [IO.File]::WriteAllBytes((Join-Path $ui "icon-$px.png"), (Draw-Frame $px)) }
Write-Host "Wrote ui\icon-32.png, icon-192.png, icon-512.png"
