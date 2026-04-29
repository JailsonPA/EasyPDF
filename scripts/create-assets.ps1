<#
.SYNOPSIS
    Generates placeholder MSIX icon assets as solid-colour PNG files.

.DESCRIPTION
    Creates the eight PNG files required by Package.appxmanifest under
    packaging\Assets\.  Run this once; replace the files with real artwork later.

    Required sizes (MSIX / Store requirements):
        Square44x44Logo.png      44 × 44
        Square150x150Logo.png   150 × 150
        Wide310x150Logo.png     310 × 150
        StoreLogo.png            50 × 50
        SplashScreen.png        620 × 300

    Scale-plate variants are also generated so Windows picks the right asset:
        Square44x44Logo.scale-200.png     88 × 88
        Square150x150Logo.scale-200.png  300 × 300
        Wide310x150Logo.scale-200.png    620 × 300

.NOTES
    Requires .NET (uses System.Drawing or WPF PngBitmapEncoder via PowerShell).
    Run from the repo root:  .\scripts\create-assets.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Windows.Forms

$outDir = Join-Path $PSScriptRoot '..\packaging\Assets'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$outDir = (Resolve-Path $outDir).Path

# Brand colours
$bgColor  = [Windows.Media.Color]::FromRgb(0x1E, 0x7E, 0xD4)   # PDF-red → use brand blue
$fgColor  = [Windows.Media.Color]::FromRgb(0xFF, 0xFF, 0xFF)

function Save-Asset {
    param(
        [string] $FileName,
        [int]    $Width,
        [int]    $Height,
        [string] $Label = ''
    )

    $dpi       = 96
    $stride    = $Width * 4
    $pixels    = New-Object byte[] ($stride * $Height)

    # Fill background
    for ($i = 0; $i -lt $pixels.Length; $i += 4) {
        $pixels[$i]   = $bgColor.B
        $pixels[$i+1] = $bgColor.G
        $pixels[$i+2] = $bgColor.R
        $pixels[$i+3] = 0xFF
    }

    $bmp = [Windows.Media.Imaging.BitmapSource]::Create(
        $Width, $Height, $dpi, $dpi,
        [Windows.Media.PixelFormats]::Bgr32,
        $null, $pixels, $stride)

    # Composite a text label using DrawingVisual so the asset is recognisable
    if ($Label) {
        $dv     = New-Object Windows.Media.DrawingVisual
        $dc     = $dv.RenderOpen()
        $brush  = New-Object Windows.Media.SolidColorBrush($bgColor)
        $dc.DrawRectangle($brush, $null,
            (New-Object Windows.Rect(0, 0, $Width, $Height)))

        $fontSize  = [Math]::Max(9, [int]($Height * 0.3))
        $typeface  = New-Object Windows.Media.Typeface('Segoe UI')
        $fgBrush   = New-Object Windows.Media.SolidColorBrush($fgColor)
        $ft = New-Object Windows.Media.FormattedText(
            $Label,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [Windows.FlowDirection]::LeftToRight,
            $typeface,
            $fontSize,
            $fgBrush,
            $dpi)
        $ft.TextAlignment = [Windows.TextAlignment]::Center
        $dc.DrawText($ft, (New-Object Windows.Point($Width / 2, ($Height - $ft.Height) / 2)))
        $dc.Close()

        $rtb = New-Object Windows.Media.Imaging.RenderTargetBitmap(
            $Width, $Height, $dpi, $dpi,
            [Windows.Media.PixelFormats]::Pbgra32)
        $rtb.Render($dv)
        $bmp = $rtb
    }

    $enc  = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $path = Join-Path $outDir $FileName
    $fs   = [IO.File]::Create($path)
    try   { $enc.Save($fs) }
    finally { $fs.Dispose() }

    Write-Host "  $FileName  (${Width}x${Height})" -ForegroundColor Cyan
}

Write-Host "`nGenerating MSIX assets → $outDir`n" -ForegroundColor Yellow

# Required baseline assets
Save-Asset 'Square44x44Logo.png'      44  44  'EP'
Save-Asset 'Square150x150Logo.png'   150 150  'EasyPDF'
Save-Asset 'Wide310x150Logo.png'     310 150  'EasyPDF'
Save-Asset 'StoreLogo.png'            50  50  'EP'
Save-Asset 'SplashScreen.png'        620 300  'EasyPDF'

# Scale-200 variants (Windows picks these on high-DPI displays)
Save-Asset 'Square44x44Logo.scale-200.png'      88  88  'EP'
Save-Asset 'Square150x150Logo.scale-200.png'   300 300  'EasyPDF'
Save-Asset 'Wide310x150Logo.scale-200.png'     620 300  'EasyPDF'

Write-Host "`nDone. Replace files in packaging\Assets\ with real artwork before publishing.`n" -ForegroundColor Green
