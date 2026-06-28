# Generates Resources\ImmichDrive.ico — a multi-resolution icon (16..256) so Windows uses a
# crisply-rendered small image in the tray instead of downscaling a single 256px bitmap.
# Bold sun + mountains "photo" motif on a blue->indigo gradient badge so it reads at 16px.
# Run with: powershell.exe -NoProfile -File generate-icon.ps1   (ASCII only)
Add-Type -AssemblyName System.Drawing

$outDir  = $PSScriptRoot
$icoPath = Join-Path $outDir "ImmichDrive.ico"

$c1 = [System.Drawing.Color]::FromArgb(255, 56, 189, 248)   # sky blue
$c2 = [System.Drawing.Color]::FromArgb(255, 99, 102, 241)   # indigo

function New-RoundedPath([System.Drawing.RectangleF]$r, [single]$radius) {
    $d = $radius * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Gradient rounded-rect badge (nearly full-bleed for max area at small sizes).
    $pad = [single]([math]::Max(0.5, $S * 0.03))
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($S - 2 * $pad), ($S - 2 * $pad))
    $radius = [single]($S * ([double]$(if ($S -le 24) { 0.16 } else { 0.22 })))
    $badge = New-RoundedPath $rect $radius
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 55.0)
    $g.FillPath($brush, $badge)

    # Keep the motif inside the badge.
    $g.SetClip($badge)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    # Sun.
    $sunR = [single]($S * 0.12)
    $sunX = [single]($S * 0.34); $sunY = [single]($S * 0.37)
    $g.FillEllipse($white, $sunX - $sunR, $sunY - $sunR, $sunR * 2, $sunR * 2)

    # Mountains (filled to the badge bottom; clip trims the overflow).
    $pts = @(
        (New-Object System.Drawing.PointF([single]($S * 0.04), [single]($S * 0.82))),
        (New-Object System.Drawing.PointF([single]($S * 0.36), [single]($S * 0.46))),
        (New-Object System.Drawing.PointF([single]($S * 0.52), [single]($S * 0.62))),
        (New-Object System.Drawing.PointF([single]($S * 0.70), [single]($S * 0.40))),
        (New-Object System.Drawing.PointF([single]($S * 0.98), [single]($S * 0.82))),
        (New-Object System.Drawing.PointF([single]($S * 0.98), [single]($S * 1.02))),
        (New-Object System.Drawing.PointF([single]($S * 0.04), [single]($S * 1.02)))
    )
    $g.FillPolygon($white, [System.Drawing.PointF[]]$pts)

    $g.ResetClip(); $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

# Build a PNG-compressed multi-image ICO (supported by Windows Vista+ at every size).
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = foreach ($s in $sizes) { ,(New-IconPng $s) }

$header = New-Object 'System.Collections.Generic.List[byte]'
function Add-U16([int]$v) { $script:header.AddRange([System.BitConverter]::GetBytes([uint16]$v)) }
function Add-U32([int64]$v) { $script:header.AddRange([System.BitConverter]::GetBytes([uint32]$v)) }

Add-U16 0; Add-U16 1; Add-U16 $sizes.Count          # reserved, type=icon, image count
$offset = 6 + 16 * $sizes.Count                       # data starts after dir + entries
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $header.Add([byte]($(if ($s -ge 256) { 0 } else { $s })))   # width  (0 = 256)
    $header.Add([byte]($(if ($s -ge 256) { 0 } else { $s })))   # height (0 = 256)
    $header.Add([byte]0); $header.Add([byte]0)                  # colors, reserved
    Add-U16 1; Add-U16 32                                       # planes, bitcount
    Add-U32 $len                                                # bytes in resource
    Add-U32 $offset                                             # offset to image data
    $offset += $len
}

$all = New-Object 'System.Collections.Generic.List[byte]'
$all.AddRange($header)
foreach ($p in $pngs) { $all.AddRange($p) }
[System.IO.File]::WriteAllBytes($icoPath, $all.ToArray())
Write-Output "Wrote $icoPath ($($all.Count) bytes, $($sizes.Count) sizes: $($sizes -join ', '))"
