# Generates the MSIX visual assets AND the app .ico from one source: the "Photo Panes" mark --
# a dark rounded tile holding a 2x2 grid where three panes are tiny photo scenes (sun + mountains)
# and the fourth is a white cloud sized to the pane column (the cloud-backed pane = a placeholder).
# Drawn on a TRANSPARENT canvas; the rounded dark tile is part of the mark.
# Run with Windows PowerShell: powershell.exe -NoProfile -File generate-msix-images.ps1
# ASCII only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).
Add-Type -AssemblyName System.Drawing

$repoRoot  = Split-Path $PSScriptRoot -Parent
$imagesDir = Join-Path $PSScriptRoot "Images"
$icoPath   = Join-Path $repoRoot "ImmichDrive\Resources\ImmichDrive.ico"
New-Item -ItemType Directory -Force $imagesDir | Out-Null

function New-Color([int]$r, [int]$g, [int]$b) {
    return [System.Drawing.Color]::FromArgb(255, $r, $g, $b)
}

$colTile   = New-Color 30 41 59     # 1E293B dark slate tile
$colCloud  = New-Color 255 255 255

# Per-pane palette: background, sun (null = none), mountain.
$panes = @(
    @{ X = 20; Y = 20; Bg = (New-Color 125 211 252); Sun = @(40, 27, 3.0);   SunCol = (New-Color 253 224 71);
       Mtn = @(20,46, 30,32, 36,39, 40,35, 46,46); MtnCol = (New-Color 3 105 161) }    # day
    @{ X = 50; Y = 20; Bg = (New-Color 253 186 116); Sun = @(68, 28, 3.5);   SunCol = (New-Color 245 158 11);
       Mtn = @(50,46, 60,33, 66,40, 70,36, 76,46); MtnCol = (New-Color 154 52 18) }    # sunset
    @{ X = 20; Y = 50; Bg = (New-Color 167 243 208); Sun = $null;            SunCol = $null;
       Mtn = @(20,76, 28,62, 34,69, 38,64, 46,76); MtnCol = (New-Color 4 120 87) }     # green hills
)

# All geometry lives in a 96x96 unit space; $f scales it to the render size.
function New-RoundedRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r, [double]$f) {
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x *= $f; $y *= $f; $w *= $f; $h *= $f; $d = 2.0 * $r * $f
    $gp.AddArc([single]$x, [single]$y, [single]$d, [single]$d, 180, 90)
    $gp.AddArc([single]($x + $w - $d), [single]$y, [single]$d, [single]$d, 270, 90)
    $gp.AddArc([single]($x + $w - $d), [single]($y + $h - $d), [single]$d, [single]$d, 0, 90)
    $gp.AddArc([single]$x, [single]($y + $h - $d), [single]$d, [single]$d, 90, 90)
    $gp.CloseFigure()
    return $gp
}

function Fill-Circle($g, $brush, [double]$cx, [double]$cy, [double]$r, [double]$f) {
    $g.FillEllipse($brush, [single](($cx - $r) * $f), [single](($cy - $r) * $f), [single](2.0 * $r * $f), [single](2.0 * $r * $f))
}

function Draw-Mark($g, [double]$f) {
    # Tile
    $tile = New-RoundedRect 0 0 96 96 22 $f
    $b = New-Object System.Drawing.SolidBrush($colTile)
    $g.FillPath($b, $tile); $b.Dispose(); $tile.Dispose()

    # Three photo panes, scenes clipped to each pane's rounded rect
    foreach ($p in $panes) {
        $path = New-RoundedRect $p.X $p.Y 26 26 6 $f
        $bg = New-Object System.Drawing.SolidBrush($p.Bg)
        $g.FillPath($bg, $path); $bg.Dispose()
        $g.SetClip($path)
        if ($p.Sun) {
            $sb = New-Object System.Drawing.SolidBrush($p.SunCol)
            Fill-Circle $g $sb $p.Sun[0] $p.Sun[1] $p.Sun[2] $f
            $sb.Dispose()
        }
        $m = $p.Mtn
        $pts = New-Object 'System.Drawing.PointF[]' ($m.Count / 2)
        for ($i = 0; $i -lt $m.Count / 2; $i++) {
            $pts[$i] = New-Object System.Drawing.PointF([single]($m[2 * $i] * $f), [single]($m[2 * $i + 1] * $f))
        }
        $mb = New-Object System.Drawing.SolidBrush($p.MtnCol)
        $g.FillPolygon($mb, $pts); $mb.Dispose()
        $g.ResetClip()
        $path.Dispose()
    }

    # Cloud pane (variant A: width-matched to the pane column, centered in its slot)
    $cb = New-Object System.Drawing.SolidBrush($colCloud)
    Fill-Circle $g $cb 57 64.5 6.0 $f
    Fill-Circle $g $cb 65 62.0 7.5 $f
    $bar = New-RoundedRect 50 64 26 9 4.5 $f
    $g.FillPath($cb, $bar); $bar.Dispose(); $cb.Dispose()
}

# Render the icon once at high resolution on a TRANSPARENT canvas; every asset is a high-quality
# downscale of this (supersampling keeps the small pane scenes crisp).
function New-Master([int]$M) {
    $bmp = New-Object System.Drawing.Bitmap($M, $M, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Mark $g ([double]$M / 96.0)
    $g.Dispose()
    return $bmp
}

function New-Resized($src, [int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $w, $h)
    $g.Dispose()
    return $bmp
}

# Render each asset at its OWN size with 4x supersampling (render large, shrink once). This keeps
# small frames crisp -- downscaling a single 1024 master all the way to 16/24 px blurs them.
$SS = 4
function New-Crisp([int]$size) {
    $m = New-Master ($size * $SS)
    $r = New-Resized $m $size $size
    $m.Dispose()
    return $r
}

function Save-Square([int]$size, [string]$name) {
    $b = New-Crisp $size
    $b.Save((Join-Path $imagesDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    Write-Output "  $name ($size x $size)"
}

function Save-Splash([int]$w, [int]$h, [string]$name) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $badge = [int]($h * 0.5); $bx = [int](($w - $badge) / 2); $by = [int](($h - $badge) / 2)
    $bb = New-Crisp $badge
    $g.DrawImage($bb, $bx, $by, $badge, $badge)
    $bb.Dispose(); $g.Dispose()
    $bmp.Save((Join-Path $imagesDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "  $name ($w x $h)"
}

function Save-Png([int]$size, [string]$path) {
    $b = New-Crisp $size
    $b.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    Write-Output "  $(Split-Path $path -Leaf) ($size x $size)"
}

function Save-Ico([int[]]$sizes, [string]$path) {
    $blobs = @()
    foreach ($s in $sizes) {
        $b = New-Crisp $s
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $blobs += ,($ms.ToArray())
        $ms.Dispose(); $b.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $blobs[$i].Length
        $wb = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$wb); $bw.Write([byte]$wb); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($b in $blobs) { $bw.Write($b) }
    $bw.Flush(); $bw.Close(); $fs.Close()
    Write-Output "  ImmichDrive.ico ($($sizes -join ', '))"
}

Write-Output "Generating MSIX images:"
Save-Square 50  "StoreLogo.png"
Save-Square 44  "Square44x44Logo.png"
Save-Square 88  "Square44x44Logo.scale-200.png"
Save-Square 24  "Square44x44Logo.targetsize-24_altform-unplated.png"
Save-Square 150 "Square150x150Logo.png"
Save-Square 300 "Square150x150Logo.scale-200.png"
Save-Splash 620 300  "SplashScreen.png"
Save-Splash 1240 600 "SplashScreen.scale-200.png"

Write-Output "Generating app icon + in-app PNG:"
Save-Ico @(16, 20, 24, 32, 40, 48, 64, 128, 256) $icoPath
Save-Png 256 (Join-Path $repoRoot "ImmichDrive\Resources\ImmichDrive.png")

Write-Output "Done."
