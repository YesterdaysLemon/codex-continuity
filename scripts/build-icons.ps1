[CmdletBinding()]
param(
    [switch]$Check,

    [string]$ExecutablePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $repositoryRoot "assets"
$iconPath = Join-Path $assetDirectory "CodexContinuity.ico"
$previewPath = Join-Path $assetDirectory "icon-256.png"
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function Assert-Icon {
    if (-not (Test-Path -LiteralPath $iconPath)) {
        throw "Missing generated icon: $iconPath"
    }
    if (-not (Test-Path -LiteralPath $previewPath)) {
        throw "Missing generated icon preview: $previewPath"
    }

    $bytes = [System.IO.File]::ReadAllBytes($iconPath)
    if ($bytes.Length -lt 6 -or $bytes[0] -ne 0 -or $bytes[1] -ne 0 -or
        $bytes[2] -ne 1 -or $bytes[3] -ne 0) {
        throw "The generated asset is not a Windows ICO file."
    }

    $imageCount = [System.BitConverter]::ToUInt16($bytes, 4)
    if ($imageCount -ne $sizes.Count) {
        throw "Expected $($sizes.Count) icon sizes; found $imageCount."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExecutablePath)) {
        $resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
        $versionInfo = (Get-Item -LiteralPath $resolvedExecutable).VersionInfo
        if ($versionInfo.FileDescription -ne "Codex Continuity Supervisor" -or
            $versionInfo.ProductName -ne "Codex Continuity" -or
            $versionInfo.CompanyName -ne "YesterdaysLemon" -or
            $versionInfo.OriginalFilename -ne "CodexContinuity.dll") {
            throw "The executable does not contain the expected Windows version resources."
        }

        Add-Type -AssemblyName System.Drawing
        $embeddedIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($resolvedExecutable)
        if ($null -eq $embeddedIcon) {
            throw "The executable does not contain an extractable icon."
        }
        try {
            $bitmap = $embeddedIcon.ToBitmap()
            try {
                $containsBrandColor = $false
                for ($x = 0; $x -lt $bitmap.Width -and -not $containsBrandColor; $x++) {
                    for ($y = 0; $y -lt $bitmap.Height; $y++) {
                        $pixel = $bitmap.GetPixel($x, $y)
                        if ($pixel.G -gt 180 -and $pixel.R -lt 190 -and $pixel.B -lt 200) {
                            $containsBrandColor = $true
                            break
                        }
                    }
                }
                if (-not $containsBrandColor) {
                    throw "The executable icon does not contain the Continuity brand color."
                }
            }
            finally {
                $bitmap.Dispose()
            }
        }
        finally {
            $embeddedIcon.Dispose()
        }
    }

    Write-Host "Icon is valid: $iconPath ($imageCount sizes)"
}

if ($Check) {
    Assert-Icon
    return
}

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null

$pngImages = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $scale = $size / 512.0
        $background = [System.Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $radius = 112 * $scale
            $diameter = 2 * $radius
            $left = 20 * $scale
            $top = 20 * $scale
            $width = 472 * $scale
            $height = 472 * $scale
            $background.AddArc($left, $top, $diameter, $diameter, 180, 90)
            $background.AddArc($left + $width - $diameter, $top, $diameter, $diameter, 270, 90)
            $background.AddArc($left + $width - $diameter, $top + $height - $diameter, $diameter, $diameter, 0, 90)
            $background.AddArc($left, $top + $height - $diameter, $diameter, $diameter, 90, 90)
            $background.CloseFigure()

            $backgroundBrush = [System.Drawing.SolidBrush]::new(
                [System.Drawing.ColorTranslator]::FromHtml("#080b09"))
            $borderPen = [System.Drawing.Pen]::new(
                [System.Drawing.ColorTranslator]::FromHtml("#26322b"),
                [Math]::Max(1, 16 * $scale))
            try {
                $graphics.FillPath($backgroundBrush, $background)
                $graphics.DrawPath($borderPen, $background)
            }
            finally {
                $backgroundBrush.Dispose()
                $borderPen.Dispose()
            }
        }
        finally {
            $background.Dispose()
        }

        $acidBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.ColorTranslator]::FromHtml("#9aff57"))
        $cyanBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.ColorTranslator]::FromHtml("#66e3ff"))
        try {
            $bars = @(
                @(112, 142, 276, 54, 400, 169),
                @(112, 229, 186, 54, 310, 256),
                @(112, 316, 96, 54, 220, 343)
            )
            foreach ($bar in $bars) {
                $graphics.FillRectangle(
                    $acidBrush,
                    [single]($bar[0] * $scale),
                    [single]($bar[1] * $scale),
                    [single]($bar[2] * $scale),
                    [single]($bar[3] * $scale))
                $circleSize = 40 * $scale
                $graphics.FillEllipse(
                    $cyanBrush,
                    [single](($bar[4] - 20) * $scale),
                    [single](($bar[5] - 20) * $scale),
                    [single]$circleSize,
                    [single]$circleSize)
            }
        }
        finally {
            $acidBrush.Dispose()
            $cyanBrush.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngBytes = $stream.ToArray()
            $pngImages.Add($pngBytes)
            if ($size -eq 256) {
                [System.IO.File]::WriteAllBytes($previewPath, $pngBytes)
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$output = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$pngImages.Count)
    $offset = 6 + (16 * $pngImages.Count)
    for ($index = 0; $index -lt $pngImages.Count; $index++) {
        $size = $sizes[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$pngImages[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $pngImages[$index].Length
    }
    foreach ($image in $pngImages) {
        $writer.Write($image)
    }
}
finally {
    $writer.Dispose()
    $output.Dispose()
}

Assert-Icon
