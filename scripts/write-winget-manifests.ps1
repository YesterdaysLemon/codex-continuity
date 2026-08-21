[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$")]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^https://")]
    [string]$InstallerUrl,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
    [string]$InstallerSha256,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^\d{4}-\d{2}-\d{2}$")]
    [string]$ReleaseDate,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$identifier = "YesterdaysLemon.CodexContinuity"
$schemaVersion = "1.12.0"
$sha256 = $InstallerSha256.ToUpperInvariant()

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schemaVersion.schema.json
PackageIdentifier: $identifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schemaVersion
"@ | Set-Content -LiteralPath (Join-Path $resolvedOutput "$identifier.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schemaVersion.schema.json
PackageIdentifier: $identifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: YesterdaysLemon
PublisherUrl: https://alirezaafshan.com
PublisherSupportUrl: https://github.com/YesterdaysLemon/codex-continuity/issues
Author: Alireza Afshan
PackageName: Codex Continuity
PackageUrl: https://continuity.alirezaafshan.com
License: MIT
LicenseUrl: https://github.com/YesterdaysLemon/codex-continuity/blob/main/LICENSE
Copyright: Copyright (c) 2026 Alireza Afshan
ShortDescription: Keep Codex agent threads alive while the Windows desktop UI updates or restarts.
Description: An unofficial per-user Windows continuity supervisor with an optional notification-area status controller. It uses the official Codex app-server over loopback and never patches or restarts the desktop app during installation.
Moniker: codex-continuity
Tags:
  - agent
  - codex
  - continuity
  - developer-tools
  - windows
ReleaseNotesUrl: https://github.com/YesterdaysLemon/codex-continuity/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schemaVersion
"@ | Set-Content -LiteralPath (Join-Path $resolvedOutput "$identifier.locale.en-US.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schemaVersion.schema.json
PackageIdentifier: $identifier
PackageVersion: $Version
InstallerType: exe
Scope: user
InstallModes:
  - interactive
  - silent
UpgradeBehavior: install
ReleaseDate: $ReleaseDate
Installers:
  - Architecture: x64
    InstallerUrl: $InstallerUrl
    InstallerSha256: $sha256
    InstallerSwitches:
      Silent: --silent --skip-self-test --no-start
      SilentWithProgress: --silent --skip-self-test --no-start
    ProductCode: CodexContinuity
    AppsAndFeaturesEntries:
      - DisplayName: Codex Continuity
        Publisher: YesterdaysLemon
        DisplayVersion: $Version
        ProductCode: CodexContinuity
        InstallerType: exe
    RepairBehavior: installer
ManifestType: installer
ManifestVersion: $schemaVersion
"@ | Set-Content -LiteralPath (Join-Path $resolvedOutput "$identifier.installer.yaml") -Encoding utf8

Write-Host "Generated WinGet review manifests at $resolvedOutput"
