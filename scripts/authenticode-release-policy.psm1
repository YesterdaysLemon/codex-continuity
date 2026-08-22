Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-NormalizedAuthenticodeThumbprint {
    param(
        [AllowNull()]
        [string]$Thumbprint
    )

    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        return $null
    }
    $normalized = ($Thumbprint -replace "\s", "").ToUpperInvariant()
    if ($normalized -notmatch "^[0-9A-F]{40}$") {
        throw "The expected Authenticode certificate thumbprint must contain exactly 40 hexadecimal characters."
    }
    return $normalized
}

function Assert-AuthenticodeReleasePolicy {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Artifacts,

        [switch]$RequireUnsigned,

        [AllowNull()]
        [string]$ExpectedThumbprint
    )

    if ($Artifacts.Count -eq 0) {
        throw "At least one release executable is required."
    }
    $normalizedExpected = ConvertTo-NormalizedAuthenticodeThumbprint $ExpectedThumbprint
    if ($RequireUnsigned) {
        if ($null -ne $normalizedExpected) {
            throw "Unsigned release mode cannot use an expected publisher thumbprint."
        }
        $signedArtifact = $Artifacts |
            Where-Object { $_.Status -ne "NotSigned" } |
            Select-Object -First 1
        if ($null -ne $signedArtifact) {
            throw "Unsigned release mode found an Authenticode signature on $($signedArtifact.Path)."
        }
        return
    }
    if ($null -eq $normalizedExpected) {
        throw "Signed release verification requires the expected publisher thumbprint."
    }

    foreach ($artifact in $Artifacts) {
        if ($artifact.Status -ne "Valid" -or
            [string]::IsNullOrWhiteSpace($artifact.SignerThumbprint)) {
            throw "Authenticode verification failed for $($artifact.Path): $($artifact.Status)"
        }
        if (-not $artifact.HasTimestamp) {
            throw "Authenticode signature is missing its RFC 3161 timestamp for $($artifact.Path)."
        }
        $actualThumbprint = ConvertTo-NormalizedAuthenticodeThumbprint $artifact.SignerThumbprint
        if ($actualThumbprint -ne $normalizedExpected) {
            throw "Authenticode signer thumbprint did not match the configured publisher for $($artifact.Path)."
        }
    }
}

function Invoke-SignToolVerification {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SignToolPath,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    & $SignToolPath verify /pa /v $Path
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool verification failed for $Path."
    }
}

Export-ModuleMember -Function `
    Assert-AuthenticodeReleasePolicy, `
    ConvertTo-NormalizedAuthenticodeThumbprint, `
    Invoke-SignToolVerification
