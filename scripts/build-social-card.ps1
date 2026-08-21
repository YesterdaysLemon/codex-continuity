[CmdletBinding()]
param(
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repositoryRoot "assets\social-card-source.png"
$outputPath = Join-Path $repositoryRoot "site\public\og.png"

Add-Type -AssemblyName System.Drawing

function Assert-SocialCard {
    $image = [System.Drawing.Image]::FromFile($outputPath)
    try {
        if ($image.Width -ne 1200 -or $image.Height -ne 630) {
            throw "Expected a 1200x630 social card; found $($image.Width)x$($image.Height)."
        }
    }
    finally {
        $image.Dispose()
    }
    Write-Host "Social card is valid: $outputPath (1200x630)"
}

if ($Check) {
    Assert-SocialCard
    return
}

$source = [System.Drawing.Image]::FromFile($sourcePath)
$output = [System.Drawing.Bitmap]::new(1200, 630)
$graphics = [System.Drawing.Graphics]::FromImage($output)
try {
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $sourceAspect = 1200.0 / 630.0
    $cropHeight = [int][Math]::Round($source.Width / $sourceAspect)
    $cropTop = [int][Math]::Floor(($source.Height - $cropHeight) / 2.0)
    $destinationRectangle = [System.Drawing.Rectangle]::new(0, 0, 1200, 630)
    $sourceRectangle = [System.Drawing.Rectangle]::new(0, $cropTop, $source.Width, $cropHeight)
    $graphics.DrawImage(
        $source,
        $destinationRectangle,
        $sourceRectangle,
        [System.Drawing.GraphicsUnit]::Pixel)
}
finally {
    $graphics.Dispose()
    $source.Dispose()
}

$temporaryPath = "$outputPath.tmp.png"
try {
    $output.Save($temporaryPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Move-Item -LiteralPath $temporaryPath -Destination $outputPath -Force
}
finally {
    $output.Dispose()
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Assert-SocialCard
