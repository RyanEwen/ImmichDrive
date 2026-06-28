# Generates the MSIX visual assets AND the app .ico from one source: a rounded 5-blade
# camera aperture in Immich's logo colors, on a dark slate tile.
# Run with Windows PowerShell: powershell.exe -NoProfile -File generate-msix-images.ps1
# ASCII only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).
Add-Type -AssemblyName System.Drawing

$repoRoot  = Split-Path $PSScriptRoot -Parent
$imagesDir = Join-Path $PSScriptRoot "Images"
$icoPath   = Join-Path $repoRoot "ImmichDrive\Resources\ImmichDrive.ico"
New-Item -ItemType Directory -Force $imagesDir | Out-Null

# Dark slate tile (#1d2230). The center hole is this same color so it vanishes into the tile,
# leaving a clean opening where the five bright blades meet.
$tile = [System.Drawing.Color]::FromArgb(255, 29, 34, 48)

# Aperture outer vertices in unit space (radius 46, centered at 0,0), as flat coord arrays.
$Vx = @(0.0, 43.7, 27.0, -27.0, -43.7)
$Vy = @(-46.0, -14.2, 37.2, 37.2, -14.2)

# Immich logo colors, one per blade (red, amber, green, blue, pink).
$colR = @(250, 255, 24, 30, 237)
$colG = @(41, 180, 194, 131, 121)
$colB = @(33, 0, 73, 247, 181)

function New-AperturePath([double]$cx, [double]$cy, [double]$f) {
    $t = 11.0
    $Ax = @(); $Ay = @(); $Bx = @(); $By = @()
    for ($i = 0; $i -lt 5; $i++) {
        $p = ($i + 4) % 5; $n = ($i + 1) % 5
        $dpx = $Vx[$p] - $Vx[$i]; $dpy = $Vy[$p] - $Vy[$i]; $lp = [math]::Sqrt($dpx * $dpx + $dpy * $dpy)
        $dnx = $Vx[$n] - $Vx[$i]; $dny = $Vy[$n] - $Vy[$i]; $ln = [math]::Sqrt($dnx * $dnx + $dny * $dny)
        $Bx += $Vx[$i] + $t * $dpx / $lp; $By += $Vy[$i] + $t * $dpy / $lp
        $Ax += $Vx[$i] + $t * $dnx / $ln; $Ay += $Vy[$i] + $t * $dny / $ln
    }
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $curx = $Ax[0]; $cury = $Ay[0]
    for ($k = 1; $k -le 5; $k++) {
        $i = $k % 5
        $gp.AddLine([single]($cx + $curx * $f), [single]($cy + $cury * $f), [single]($cx + $Bx[$i] * $f), [single]($cy + $By[$i] * $f))
        $c1x = $Bx[$i] + (2.0 / 3.0) * ($Vx[$i] - $Bx[$i]); $c1y = $By[$i] + (2.0 / 3.0) * ($Vy[$i] - $By[$i])
        $c2x = $Ax[$i] + (2.0 / 3.0) * ($Vx[$i] - $Ax[$i]); $c2y = $Ay[$i] + (2.0 / 3.0) * ($Vy[$i] - $Ay[$i])
        $gp.AddBezier(
            [single]($cx + $Bx[$i] * $f), [single]($cy + $By[$i] * $f),
            [single]($cx + $c1x * $f), [single]($cy + $c1y * $f),
            [single]($cx + $c2x * $f), [single]($cy + $c2y * $f),
            [single]($cx + $Ax[$i] * $f), [single]($cy + $Ay[$i] * $f))
        $curx = $Ax[$i]; $cury = $Ay[$i]
    }
    $gp.CloseFigure()
    return $gp
}

function Draw-Aperture($g, [double]$cx, [double]$cy, [double]$S) {
    $f = $S / 46.0
    $rp = New-AperturePath $cx $cy $f
    $g.SetClip($rp)
    for ($i = 0; $i -lt 5; $i++) {
        $j = ($i + 1) % 5
        $pts = New-Object 'System.Drawing.PointF[]' 3
        $pts[0] = New-Object System.Drawing.PointF([single]$cx, [single]$cy)
        $pts[1] = New-Object System.Drawing.PointF([single]($cx + $Vx[$i] * $f), [single]($cy + $Vy[$i] * $f))
        $pts[2] = New-Object System.Drawing.PointF([single]($cx + $Vx[$j] * $f), [single]($cy + $Vy[$j] * $f))
        $b = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, $colR[$i], $colG[$i], $colB[$i]))
        $g.FillPolygon($b, $pts); $b.Dispose()
    }
    $g.ResetClip(); $rp.Dispose()
    $hb = New-Object System.Drawing.SolidBrush($tile)
    $hr = 14.0 * $f
    $g.FillEllipse($hb, [single]($cx - $hr), [single]($cy - $hr), [single]($hr * 2), [single]($hr * 2))
    $hb.Dispose()
}

# Render the icon once at high resolution; every asset is a high-quality downscale of this
# (supersampling smooths the clipped aperture edge, which GDI+ region-clipping aliases).
function New-Master([int]$M) {
    $bmp = New-Object System.Drawing.Bitmap($M, $M, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $rad = [double]$M * 0.18; $d = $rad * 2
    $tp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tp.AddArc(0, 0, $d, $d, 180, 90)
    $tp.AddArc($M - $d, 0, $d, $d, 270, 90)
    $tp.AddArc($M - $d, $M - $d, $d, $d, 0, 90)
    $tp.AddArc(0, $M - $d, $d, $d, 90, 90)
    $tp.CloseFigure()
    $tb = New-Object System.Drawing.SolidBrush($tile)
    $g.FillPath($tb, $tp); $tb.Dispose(); $tp.Dispose()
    Draw-Aperture $g ([double]$M / 2) ([double]$M / 2) ([double]$M * 0.34)
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
