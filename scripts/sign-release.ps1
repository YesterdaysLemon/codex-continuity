[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Paths,

    [switch]$VerifyOnly,

    [switch]$RequireUnsigned,

    [string]$ExpectedThumbprint = $env:CONTINUITY_SIGNING_EXPECTED_THUMBPRINT
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "authenticode-release-policy.psm1") -Force

$resolvedPaths = @($Paths | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
if ($resolvedPaths.Count -eq 0) {
    throw "At least one release executable is required."
}

$normalizedExpectedThumbprint = ConvertTo-NormalizedAuthenticodeThumbprint $ExpectedThumbprint

if ($RequireUnsigned -and -not $VerifyOnly) {
    throw "RequireUnsigned can only be used with VerifyOnly."
}

$certificateBase64 = $env:CONTINUITY_SIGNING_CERTIFICATE_BASE64
$certificatePassword = $env:CONTINUITY_SIGNING_CERTIFICATE_PASSWORD
if (-not $VerifyOnly) {
    $missingConfiguration = @()
    if ([string]::IsNullOrWhiteSpace($certificateBase64)) {
        $missingConfiguration += "certificate"
    }
    if ([string]::IsNullOrWhiteSpace($certificatePassword)) {
        $missingConfiguration += "password"
    }
    if ($null -eq $normalizedExpectedThumbprint) {
        $missingConfiguration += "expected thumbprint"
    }
    if ($missingConfiguration.Count -gt 0) {
        throw "Signing configuration is incomplete. Missing: $($missingConfiguration -join ', ')."
    }
}

$signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" `
    -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object { $_.VersionInfo.FileVersionRaw } -Descending |
    Select-Object -First 1
if ($null -eq $signTool -and -not $VerifyOnly) {
    throw "Windows SDK signtool.exe was not found."
}

$certificatePath = $null
try {
    if (-not $VerifyOnly) {
        $certificatePath = Join-Path ([System.IO.Path]::GetTempPath()) (
            "codex-continuity-signing-" + [System.Guid]::NewGuid().ToString("N") + ".pfx")
        [System.IO.File]::WriteAllBytes(
            $certificatePath,
            [System.Convert]::FromBase64String($certificateBase64))

        foreach ($path in $resolvedPaths) {
            & $signTool.FullName sign /f $certificatePath /p $certificatePassword `
                /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
                /d "Codex Continuity" `
                /du "https://continuity.alirezaafshan.com" `
                $path
            if ($LASTEXITCODE -ne 0) {
                throw "Authenticode signing failed for $path."
            }
        }
    }

    $signatures = @($resolvedPaths | ForEach-Object {
            [PSCustomObject]@{
                Path      = $_
                Signature = Get-AuthenticodeSignature -LiteralPath $_
            }
        })
    $policyArtifacts = @($signatures | ForEach-Object {
            [PSCustomObject]@{
                Path             = $_.Path
                Status           = $_.Signature.Status.ToString()
                SignerThumbprint = if ($null -eq $_.Signature.SignerCertificate) {
                    $null
                } else {
                    $_.Signature.SignerCertificate.Thumbprint
                }
                HasTimestamp     = $null -ne $_.Signature.TimeStamperCertificate
            }
        })
    Assert-AuthenticodeReleasePolicy `
        -Artifacts $policyArtifacts `
        -RequireUnsigned:$RequireUnsigned `
        -ExpectedThumbprint $normalizedExpectedThumbprint
    if ($RequireUnsigned) {
        foreach ($artifact in $signatures) {
            Write-Warning "Verified unsigned development artifact: $($artifact.Path)"
        }
        return
    }

    foreach ($artifact in $signatures) {
        $path = $artifact.Path
        $signature = $artifact.Signature
        $thumbprint = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
        if ($null -eq $signTool) {
            throw "Windows SDK signtool.exe is required to verify signed artifacts."
        }
        Invoke-SignToolVerification -SignToolPath $signTool.FullName -Path $path
        Write-Host "Verified Authenticode signature for $path ($thumbprint)"
    }
}
finally {
    if ($null -ne $certificatePath -and (Test-Path -LiteralPath $certificatePath)) {
        Remove-Item -LiteralPath $certificatePath -Force
    }
}
