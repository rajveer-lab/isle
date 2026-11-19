# Draws the Isle icon (mint tile with a black island pill) and writes a multi-size PNG-compressed .ico
param([Parameter(Mandatory = $true)][string]$Out)

Add-Type -AssemblyName System.Drawing

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # tile
    $tile = New-RoundRect 0 0 ($s - 0.01) ($s - 0.01) ([single]($s * 0.24))
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $c1 = [System.Drawing.Color]::FromArgb(255, 140, 246, 204)
    $c2 = [System.Drawing.Color]::FromArgb(255, 38, 168, 150)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, ([single]90)
    $g.FillPath($grad, $tile)

    # island pill
    $pw = [single]($s * 0.74); $ph = [single]($s * 0.30)
    $px = [single](($s - $pw) / 2); $py = [single](($s - $ph) / 2)
    $pill = New-RoundRect $px $py $pw $ph ([single]($ph / 2))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 8, 8, 10))), $pill)

    if ($s -ge 24) {
        # album dot + bars inside the pill
        $mint = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 124, 242, 194))
        $dot = [single]($ph * 0.56)
        $g.FillEllipse($mint, [single]($px + $ph * 0.22), [single]($py + ($ph - $dot) / 2), $dot, $dot)
        $bw = [single]($s * 0.045); $gap = [single]($s * 0.035)
        $heights = 0.45, 0.8, 0.6
        $bx = [single]($px + $pw - $ph * 0.30 - 3 * $bw - 2 * $gap)
        for ($i = 0; $i -lt 3; $i++) {
            $bh = [single]($ph * $heights[$i])
            $bar = New-RoundRect ([single]($bx + $i * ($bw + $gap))) ([single]($py + ($ph - $bh) / 2)) $bw $bh ([single]($bw / 2))
            $g.FillPath($mint, $bar)
        }
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) { $pngs.Add((New-IconPng $s)) }

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"Icon written: $Out"
