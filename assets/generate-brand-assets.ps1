<#
    Generates every KidShell brand raster asset from code.

    Nothing in assets/ is downloaded or copied from the web: the tiles, the
    icon and the splash screen are all drawn here with System.Drawing, so the
    repository stays free of third-party image licences.

    Usage (from the repository root):
        powershell -NoProfile -ExecutionPolicy Bypass -File assets\generate-brand-assets.ps1
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $RepositoryRoot = Split-Path -Parent $scriptDir
}

Add-Type -AssemblyName System.Drawing

$iconsDir = Join-Path $RepositoryRoot 'assets\icons'
$appAssetsDir = Join-Path $RepositoryRoot 'src\KidShell.App\Assets'
New-Item -ItemType Directory -Force -Path $iconsDir, $appAssetsDir | Out-Null

# KidShell brand palette (see src/KidShell.App/Themes/Tokens.xaml).
$brandTop = [System.Drawing.Color]::FromArgb(255, 56, 152, 236)
$brandBottom = [System.Drawing.Color]::FromArgb(255, 22, 104, 193)
$spark = [System.Drawing.Color]::FromArgb(255, 251, 203, 58)
$skyTop = [System.Drawing.Color]::FromArgb(255, 199, 231, 249)
$skyBottom = [System.Drawing.Color]::FromArgb(255, 233, 244, 252)

function New-RoundedPath {
    param([System.Drawing.RectangleF]$Rect, [float]$Radius)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2
    $path.AddArc($Rect.X, $Rect.Y, $d, $d, 180, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Y, $d, $d, 270, 90)
    $path.AddArc($Rect.Right - $d, $Rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($Rect.X, $Rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-BrandBitmap {
    param(
        [int]$Width,
        [int]$Height,
        [switch]$Splash
    )

    $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $side = [Math]::Min($Width, $Height)

    if ($Splash) {
        $bgRect = New-Object System.Drawing.RectangleF(0, 0, $Width, $Height)
        $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bgRect, $skyTop, $skyBottom, 90)
        $g.FillRectangle($bgBrush, $bgRect)
        $bgBrush.Dispose()
        $tileSide = [int]($side * 0.62)
    }
    else {
        $tileSide = $side
    }

    $tileX = ($Width - $tileSide) / 2.0
    $tileY = ($Height - $tileSide) / 2.0
    $inset = $tileSide * 0.06
    $tileRect = New-Object System.Drawing.RectangleF(
        ($tileX + $inset), ($tileY + $inset), ($tileSide - 2 * $inset), ($tileSide - 2 * $inset))
    $radius = $tileRect.Width * 0.26

    $path = New-RoundedPath -Rect $tileRect -Radius $radius
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($tileRect, $brandTop, $brandBottom, 90)
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()

    # The "K" wordmark.
    $fontSize = $tileRect.Height * 0.58
    $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $textRect = New-Object System.Drawing.RectangleF(
        $tileRect.X, ($tileRect.Y + $tileRect.Height * 0.02), $tileRect.Width, $tileRect.Height)
    $g.DrawString('K', $font, $white, $textRect, $format)
    $white.Dispose()
    $font.Dispose()
    $format.Dispose()

    # Sparkle, echoing the Barnläge logo.
    if ($tileRect.Width -ge 40) {
        $sparkBrush = New-Object System.Drawing.SolidBrush $spark
        $r = $tileRect.Width * 0.075
        $cx = $tileRect.Right - $tileRect.Width * 0.24
        $cy = $tileRect.Y + $tileRect.Height * 0.24
        $g.FillEllipse($sparkBrush, ($cx - $r), ($cy - $r), (2 * $r), (2 * $r))
        $sparkBrush.Dispose()
    }

    $g.Dispose()
    return $bmp
}

$tiles = @(
    @{ Name = 'StoreLogo.png'; W = 50; H = 50 },
    @{ Name = 'Square44x44Logo.png'; W = 44; H = 44 },
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; W = 24; H = 24 },
    @{ Name = 'Square150x150Logo.png'; W = 150; H = 150 },
    @{ Name = 'SmallTile.png'; W = 71; H = 71 },
    @{ Name = 'LargeTile.png'; W = 310; H = 310 },
    @{ Name = 'Wide310x150Logo.png'; W = 310; H = 150 }
)

foreach ($tile in $tiles) {
    $bmp = New-BrandBitmap -Width $tile.W -Height $tile.H
    $bmp.Save((Join-Path $appAssetsDir $tile.Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  wrote $($tile.Name)"
}

$splash = New-BrandBitmap -Width 620 -Height 300 -Splash
$splash.Save((Join-Path $appAssetsDir 'SplashScreen.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$splash.Dispose()
Write-Host '  wrote SplashScreen.png'

# Master brand mark kept under assets/icons for future design work.
$master = New-BrandBitmap -Width 512 -Height 512
$master.Save((Join-Path $iconsDir 'kidshell-mark-512.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# Multi-resolution .ico for the window/taskbar icon.
$icoPath = Join-Path $appAssetsDir 'KidShell.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$streams = @()
$images = @()
foreach ($size in $sizes) {
    $bmp = New-BrandBitmap -Width $size -Height $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $streams += , $ms
    $images += , $bmp
}

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $bytes = $streams[$i].ToArray()
    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bw.Write($streams[$i].ToArray())
}
$bw.Flush()
$bw.Dispose()
$fs.Dispose()
foreach ($s in $streams) { $s.Dispose() }
foreach ($b in $images) { $b.Dispose() }
$master.Dispose()

Write-Host "  wrote KidShell.ico"
Write-Host "KidShell brand assets generated."
