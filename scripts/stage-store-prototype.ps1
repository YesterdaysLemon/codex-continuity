[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$IdentityName,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^CN=')]
    [string]$Publisher,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PublisherDisplayName,

    [string]$OutputDirectory = 'artifacts/store-prototype'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'CodexContinuity.csproj'
$trayProjectPath = Join-Path $repositoryRoot 'tray\CodexContinuity.Tray.csproj'
$templatePath = Join-Path $repositoryRoot 'packaging\msix\Package.appxmanifest.template.xml'
$resolvedOutput = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))

if (-not $resolvedOutput.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Prototype output must stay below $artifactRoot."
}

$publishRoot = Join-Path $artifactRoot 'store-prototype-publish'
$supervisorPublish = Join-Path $publishRoot 'supervisor'
$trayPublish = Join-Path $publishRoot 'tray'

dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=false --output $supervisorPublish
if ($LASTEXITCODE -ne 0) {
    throw 'Supervisor publish failed.'
}
dotnet publish $trayProjectPath --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=false --output $trayPublish
if ($LASTEXITCODE -ne 0) {
    throw 'Tray publish failed.'
}

$preflightOutput = & (Join-Path $supervisorPublish 'CodexContinuity.exe') store-readiness
$preflightExitCode = $LASTEXITCODE
$preflight = $preflightOutput | ConvertFrom-Json
if ($preflightExitCode -ne 2 -or $preflight.readyForSubmission -ne $false) {
    throw 'The Store prototype expected an explicit blocked readiness result.'
}
$packageVersion = $preflight.proposedPackageVersion
if ($packageVersion -notmatch '^[1-9]\d{0,4}\.(?:0|[1-9]\d{0,4})\.(?:0|[1-9]\d{0,4})\.0$') {
    throw "Store preflight returned an invalid package version: '$packageVersion'."
}

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $resolvedOutput 'Assets') -Force | Out-Null
Copy-Item -LiteralPath $supervisorPublish -Destination (Join-Path $resolvedOutput 'Supervisor') `
    -Recurse -Force
Copy-Item -LiteralPath $trayPublish -Destination (Join-Path $resolvedOutput 'Tray') `
    -Recurse -Force

function Write-SquareLogo {
    param(
        [Parameter(Mandatory = $true)] [string]$Source,
        [Parameter(Mandatory = $true)] [string]$Destination,
        [Parameter(Mandatory = $true)] [int]$Size
    )
    $sourceImage = [Drawing.Image]::FromFile($Source)
    try {
        $bitmap = New-Object Drawing.Bitmap($Size, $Size)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.DrawImage($sourceImage, 0, 0, $Size, $Size)
            }
            finally {
                $graphics.Dispose()
            }
            $bitmap.Save($Destination, [Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $sourceImage.Dispose()
    }
}

$logoSource = Join-Path $repositoryRoot 'assets\icon-256.png'
Write-SquareLogo $logoSource (Join-Path $resolvedOutput 'Assets\StoreLogo.png') 50
Write-SquareLogo $logoSource (Join-Path $resolvedOutput 'Assets\Square44x44Logo.png') 44
Write-SquareLogo $logoSource (Join-Path $resolvedOutput 'Assets\Square150x150Logo.png') 150

$manifest = Get-Content -LiteralPath $templatePath -Raw
$manifest = $manifest.Replace('{{IDENTITY_NAME}}', [Security.SecurityElement]::Escape($IdentityName))
$manifest = $manifest.Replace('{{PUBLISHER}}', [Security.SecurityElement]::Escape($Publisher))
$manifest = $manifest.Replace(
    '{{PUBLISHER_DISPLAY_NAME}}',
    [Security.SecurityElement]::Escape($PublisherDisplayName))
$manifest = $manifest.Replace('{{PACKAGE_VERSION}}', $packageVersion)
$null = [xml]$manifest
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $resolvedOutput 'AppxManifest.xml'), $manifest, $utf8NoBom)
[IO.File]::WriteAllText(
    (Join-Path $resolvedOutput 'DO-NOT-SUBMIT.txt'),
    "Store readiness is blocked. This directory is an architecture prototype, not a distributable package.`n",
    $utf8NoBom)
[IO.File]::WriteAllText(
    (Join-Path $resolvedOutput 'store-readiness.json'),
    (([ordered]@{
        stagedArtifact = 'nonShippablePackagedPrototype'
        runtimePreflight = $preflight
    } | ConvertTo-Json -Depth 9) + "`n"),
    $utf8NoBom)

Write-Host "Staged non-shippable Store prototype at $resolvedOutput"
Write-Host 'No MSIX was produced because clean endpoint restoration is not proven.'
$global:LASTEXITCODE = 0
