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

$resolvedPaths = @($Paths | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
if ($resolvedPaths.Count -eq 0) {
    throw "At least one release executable is required."
}

$normalizedExpectedThumbprint = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
    $normalizedExpectedThumbprint = ($ExpectedThumbprint -replace "\s", "").ToUpperInvariant()
    if ($normalizedExpectedThumbprint -notmatch "^[0-9A-F]{40}$") {
        throw "The expected Authenticode certificate thumbprint must contain exactly 40 hexadecimal characters."
    }
}

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
    if ($RequireUnsigned) {
        $signedArtifact = $signatures |
            Where-Object { $_.Signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned } |
            Select-Object -First 1
        if ($null -ne $signedArtifact) {
            throw "Unsigned release mode found an Authenticode signature on $($signedArtifact.Path)."
        }
        foreach ($artifact in $signatures) {
            Write-Warning "Verified unsigned development artifact: $($artifact.Path)"
        }
        return
    }

    $expectedThumbprint = $null
    foreach ($artifact in $signatures) {
        $path = $artifact.Path
        $signature = $artifact.Signature
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $null -eq $signature.SignerCertificate) {
            throw "Authenticode verification failed for ${path}: $($signature.Status)"
        }
        if ($null -eq $signature.TimeStamperCertificate) {
            throw "Authenticode signature is missing its RFC 3161 timestamp for $path."
        }
        $thumbprint = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
        if ($null -ne $normalizedExpectedThumbprint -and
            $thumbprint -ne $normalizedExpectedThumbprint) {
            throw "Authenticode signer thumbprint did not match the configured publisher for $path."
        }
        if ($null -eq $expectedThumbprint) {
            $expectedThumbprint = $thumbprint
        }
        elseif ($thumbprint -ne $expectedThumbprint) {
            throw "Release executables were signed by different certificates."
        }
        if ($null -eq $signTool) {
            throw "Windows SDK signtool.exe is required to verify signed artifacts."
        }
        & $signTool.FullName verify /pa /v $path
        if ($LASTEXITCODE -ne 0) {
            throw "SignTool verification failed for $path."
        }
        Write-Host "Verified Authenticode signature for $path ($thumbprint)"
    }
}
finally {
    if ($null -ne $certificatePath -and (Test-Path -LiteralPath $certificatePath)) {
        Remove-Item -LiteralPath $certificatePath -Force
    }
}
