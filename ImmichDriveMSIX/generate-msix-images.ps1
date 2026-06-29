# Generates the MSIX visual assets AND the app .ico from one source: a 5-blade camera iris in
# Immich's logo colors, on a TRANSPARENT background (no tile). The blades are angled off-center
# into a small rotated-pentagon opening, and each blade is outlined so the black reads as the
# space between the colors (like the Immich logo's petals).
# Run with Windows PowerShell: powershell.exe -NoProfile -File generate-msix-images.ps1
# ASCII only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).
Add-Type -AssemblyName System.Drawing

$repoRoot  = Split-Path $PSScriptRoot -Parent
$imagesDir = Join-Path $PSScriptRoot "Images"
$icoPath   = Join-Path $repoRoot "ImmichDrive\Resources\ImmichDrive.ico"
New-Item -ItemType Directory -Force $imagesDir | Out-Null

# Aperture outer vertices in unit space (radius 46, centered at 0,0), at angles -90 + 72*i.
$Vx = @(0.0, 43.7, 27.0, -27.0, -43.7)
$Vy = @(-46.0, -14.2, 37.2, 37.2, -14.2)

# Inner opening: a small pentagon whose vertices are twisted off the radial direction, so each
# blade seam runs A_i -> B_i at an angle instead of straight to the center (a real iris).
$Bx = @(); $By = @()
for ($i = 0; $i -lt 5; $i++) {
    $ang = (-90.0 + 72.0 * $i + 40.0) * [math]::PI / 180.0   # +40 deg twist
    $Bx += 8.0 * [math]::Cos($ang)                            # opening radius 8
    $By += 8.0 * [math]::Sin($ang)
}

# Immich logo colors, one per blade (amber, green, blue, pink, red).
$colR = @(255, 24, 30, 237, 250)
$colG = @(180, 194, 131, 121, 41)
$colB = @(0, 73, 247, 181, 33)

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
    $black = [System.Drawing.Color]::FromArgb(255, 0, 0, 0)
    $ow = [single]([math]::Max(1.0, 2.0 * $f))   # outline width = the black gap between colors
    $rp = New-AperturePath $cx $cy $f            # rounded pentagon (outer silhouette)

    $pen = New-Object System.Drawing.Pen($black, $ow)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # Blades clipped to the rounded pentagon. Each blade is the quad A_i, A_{i+1}, B_{i+1}, B_i;
    # filling + black-outlining each one makes the black read as the gap between the colors.
    $g.SetClip($rp)
    for ($i = 0; $i -lt 5; $i++) {
        $j = ($i + 1) % 5
        $pts = New-Object 'System.Drawing.PointF[]' 4
        $pts[0] = New-Object System.Drawing.PointF([single]($cx + $Vx[$i] * $f), [single]($cy + $Vy[$i] * $f))
        $pts[1] = New-Object System.Drawing.PointF([single]($cx + $Vx[$j] * $f), [single]($cy + $Vy[$j] * $f))
        $pts[2] = New-Object System.Drawing.PointF([single]($cx + $Bx[$j] * $f), [single]($cy + $By[$j] * $f))
        $pts[3] = New-Object System.Drawing.PointF([single]($cx + $Bx[$i] * $f), [single]($cy + $By[$i] * $f))
        $b = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, $colR[$i], $colG[$i], $colB[$i]))
        $g.FillPolygon($b, $pts); $b.Dispose()
        $g.DrawPolygon($pen, $pts)
    }

    # The opening (small inner pentagon), filled black.
    $inner = New-Object 'System.Drawing.PointF[]' 5
    for ($i = 0; $i -lt 5; $i++) {
        $inner[$i] = New-Object System.Drawing.PointF([single]($cx + $Bx[$i] * $f), [single]($cy + $By[$i] * $f))
    }
    $blk = New-Object System.Drawing.SolidBrush($black)
    $g.FillPolygon($blk, $inner); $blk.Dispose()
    $pen.Dispose()
    $g.ResetClip()

    # Outer rim outline (rounded pentagon), so the edge border matches the inner gaps.
    $rim = New-Object System.Drawing.Pen($black, $ow)
    $rim.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($rim, $rp)
    $rim.Dispose(); $rp.Dispose()
}

# Render the icon once at high resolution on a TRANSPARENT canvas; every asset is a high-quality
# downscale of this (supersampling smooths the clipped aperture edge that GDI+ region-clipping aliases).
function New-Master([int]$M) {
    $bmp = New-Object System.Drawing.Bitmap($M, $M, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Aperture $g ([double]$M / 2) ([double]$M / 2) ([double]$M * 0.45)
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
