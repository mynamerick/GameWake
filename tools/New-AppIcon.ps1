<#
.SYNOPSIS
    Regenerates assets/GameWake.ico. Run manually when the icon design changes; the build does not run it.
    The glyph geometry matches GameWake.App/Services/TrayIconFactory.cs (256-unit design grid).
#>
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\assets\GameWake.ico')
)

Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    $background = New-RoundedRectPath 8 8 240 240 56
    $top = [System.Drawing.Color]::FromArgb(255, 0xA7, 0x8B, 0xFA)
    $bottom = [System.Drawing.Color]::FromArgb(255, 0x6D, 0x28, 0xD9)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 8)), ([System.Drawing.PointF]::new(0, 248)), $top, $bottom
    $g.FillPath($brush, $background)

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $shaft = New-RoundedRectPath 110 48 36 92 10
    $g.FillPath($white, $shaft)
    $head = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(66, 122),
        [System.Drawing.PointF]::new(190, 122),
        [System.Drawing.PointF]::new(128, 186))
    $g.FillPolygon($white, $head)
    $bar = New-RoundedRectPath 66 200 124 24 12
    $g.FillPath($white, $bar)

    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # Large frames are stored as PNG, as Windows expects.
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        # Small frames are stored as 32-bit DIBs (bottom-up BGRA plus an empty AND mask) for maximum compatibility.
        $w = New-Object System.IO.BinaryWriter $stream
        $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2))
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
        $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
        $w.Write([uint32]($size * $size * 4 + $maskStride * $size))
        $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bitmap.GetPixel($x, $y)
                $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
            }
        }
        $w.Write((New-Object byte[] ($maskStride * $size)))
        $w.Flush()
    }

    $bitmap.Dispose()
    $images.Add($stream.ToArray())
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) { New-IconPng $size }

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Flush()

$full = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($full)) | Out-Null
[System.IO.File]::WriteAllBytes($full, $out.ToArray())
Write-Host "Wrote $full ($($out.Length) bytes)"
