# Generates the MSIX visual assets (gradient + photo glyph) into Images\.
# Run with Windows PowerShell: powershell.exe -NoProfile -File generate-msix-images.ps1
# ASCII only.
Add-Type -AssemblyName System.Drawing

$imagesDir = Join-Path $PSScriptRoot "Images"
New-Item -ItemType Directory -Force $imagesDir | Out-Null

$c1 = [System.Drawing.Color]::FromArgb(255, 56, 189, 248)   # sky blue
$c2 = [System.Drawing.Color]::FromArgb(255, 99, 102, 241)   # indigo
$glyph = [char]0xEB9F                                        # photo/picture

function Draw-Glyph($g, $rect, $fontPx) {
    $font = New-Object System.Drawing.Font("Segoe Fluent Icons", [single]$fontPx, [System.Drawing.GraphicsUnit]::Pixel)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $fmt = New-Object System.Drawing.StringFormat([System.Drawing.StringFormat]::GenericTypographic)
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString([string]$glyph, $font, $white, $rect, $fmt)
}

function New-Square([int]$size, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 55.0)
    $g.FillRectangle($brush, $rect)
    $rectF = New-Object System.Drawing.RectangleF(0, 0, [single]$size, [single]$size)
    Draw-Glyph $g $rectF ([single]($size * 0.52))
    $g.Dispose()
    $bmp.Save((Join-Path $imagesDir $path), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "  $path ($size x $size)"
}

function New-Splash([int]$w, [int]$h, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)
    $badge = [int]($h * 0.5)
    $bx = [int](($w - $badge) / 2); $by = [int](($h - $badge) / 2)
    $rect = New-Object System.Drawing.Rectangle($bx, $by, $badge, $badge)
    $radius = [int]($badge * 0.22); $d = $radius * 2
    $path2 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path2.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path2.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path2.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path2.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path2.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 55.0)
    $g.FillPath($brush, $path2)
    $rectF = New-Object System.Drawing.RectangleF([single]$bx, [single]$by, [single]$badge, [single]$badge)
    Draw-Glyph $g $rectF ([single]($badge * 0.52))
    $g.Dispose()
    $bmp.Save((Join-Path $imagesDir $path), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "  $path ($w x $h)"
}

Write-Output "Generating MSIX images:"
New-Square 50  "StoreLogo.png"
New-Square 44  "Square44x44Logo.png"
New-Square 88  "Square44x44Logo.scale-200.png"
New-Square 24  "Square44x44Logo.targetsize-24_altform-unplated.png"
New-Square 150 "Square150x150Logo.png"
New-Square 300 "Square150x150Logo.scale-200.png"
New-Splash 620 300  "SplashScreen.png"
New-Splash 1240 600 "SplashScreen.scale-200.png"
Write-Output "Done."
