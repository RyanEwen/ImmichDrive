# Generates Resources\ImmichDrive.ico — a gradient badge with a photo/cloud glyph.
# Run with: powershell.exe -NoProfile -File generate-icon.ps1
# ASCII only (Windows PowerShell 5.1 reads BOM-less .ps1 as ANSI).
Add-Type -AssemblyName System.Drawing

$outDir = $PSScriptRoot
$icoPath = Join-Path $outDir "ImmichDrive.ico"

$c1 = [System.Drawing.Color]::FromArgb(255, 56, 189, 248)   # sky blue
$c2 = [System.Drawing.Color]::FromArgb(255, 99, 102, 241)   # indigo
$glyph = [char]0xEB9F                                        # Segoe Fluent: photo/picture

function New-Png([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-rectangle gradient badge.
    $pad = [int]($size * 0.06)
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
    $radius = [int]($size * 0.22); $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 55.0)
    $g.FillPath($brush, $path)

    # Centered glyph.
    $font = New-Object System.Drawing.Font("Segoe Fluent Icons", [single]($size * 0.5), [System.Drawing.GraphicsUnit]::Pixel)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rectF = New-Object System.Drawing.RectangleF(0, 0, [single]$size, [single]$size)
    $g.DrawString([string]$glyph, $font, $white, $rectF, $fmt)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ms.ToArray()
}

# Build a PNG-in-ICO with a single 256x256 entry (supported by modern Windows).
# Assemble the bytes explicitly to avoid BinaryWriter overload pitfalls.
[byte[]]$png = New-Png 256
$hdr = New-Object 'System.Collections.Generic.List[byte]'
function Add-U16([int]$v) { $script:hdr.AddRange([System.BitConverter]::GetBytes([uint16]$v)) }
function Add-U32([int64]$v) { $script:hdr.AddRange([System.BitConverter]::GetBytes([uint32]$v)) }
Add-U16 0; Add-U16 1; Add-U16 1          # reserved, type=icon, count=1
$hdr.Add([byte]0); $hdr.Add([byte]0)     # width/height (0 = 256)
$hdr.Add([byte]0); $hdr.Add([byte]0)     # colors, reserved
Add-U16 1; Add-U16 32                     # planes, bitcount
Add-U32 $png.Length                       # bytes in resource
Add-U32 22                                # image offset (6 + 16)
$all = New-Object 'System.Collections.Generic.List[byte]'
$all.AddRange($hdr); $all.AddRange($png)
[System.IO.File]::WriteAllBytes($icoPath, $all.ToArray())
Write-Output "Wrote $icoPath ($($all.Count) bytes total, $($png.Length) PNG)"
