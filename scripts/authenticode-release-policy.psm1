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

function ConvertTo-NormalizedAuthenticodeSubscriberIdentityEku {
    param(
        [AllowNull()]
        [string]$Eku
    )

    if ([string]::IsNullOrWhiteSpace($Eku)) {
        return $null
    }

    $normalized = $Eku.Trim()
    $prefix = "1.3.6.1.4.1.311.97."
    $publicTrustMarker = "1.3.6.1.4.1.311.97.1.0"
    if (-not $normalized.StartsWith($prefix, [System.StringComparison]::Ordinal) -or
        $normalized -eq $publicTrustMarker) {
        throw "The Artifact Signing subscriber identity EKU must begin with 1.3.6.1.4.1.311.97. and must not be the Public Trust marker."
    }

    $suffixSegments = @($normalized.Substring($prefix.Length).Split('.'))
    $invalidSuffixSegments = @(
        $suffixSegments | Where-Object {
            if ($_.Length -eq 0) {
                return $true
            }
            foreach ($character in $_.ToCharArray()) {
                if ([int]$character -lt [int][char]'0' -or
                    [int]$character -gt [int][char]'9') {
                    return $true
                }
            }
            return $false
        }
    )
    if ($suffixSegments.Count -eq 0 -or $invalidSuffixSegments.Count -gt 0) {
        throw "The Artifact Signing subscriber identity EKU must be a complete dotted numeric OID."
    }
    return $normalized
}

function Get-AuthenticodeEnhancedKeyUsageValues {
    param(
        [AllowNull()]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if ($null -eq $Certificate) {
        return @()
    }

    return @(
        $Certificate.Extensions |
            Where-Object { $_.Oid.Value -eq "2.5.29.37" } |
            ForEach-Object { $_.EnhancedKeyUsages } |
            ForEach-Object { $_.Value } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
}

function Get-AuthenticodeChainRootThumbprint {
    param(
        [AllowNull()]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate
    )

    if ($null -eq $Certificate) {
        return $null
    }

    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        # Get-AuthenticodeSignature has already applied the Authenticode trust
        # policy. Ignore time validity only while discovering the stable root:
        # Artifact Signing's leaf certificates are intentionally short-lived,
        # and their RFC 3161 timestamp preserves the signature beyond expiry.
        $chain.ChainPolicy.RevocationMode =
            [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $chain.ChainPolicy.VerificationFlags =
            [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::IgnoreNotTimeValid
        $chainBuilt = $chain.Build($Certificate)
        if (-not $chainBuilt -or $chain.ChainElements.Count -eq 0) {
            return $null
        }
        return $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate.Thumbprint
    }
    finally {
        $chain.Dispose()
    }
}

function Get-AuthenticodePolicyArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Paths
    )

    foreach ($path in $Paths) {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        $certificate = $signature.SignerCertificate
        $ekuValues = @(Get-AuthenticodeEnhancedKeyUsageValues -Certificate $certificate)
        $subscriberIdentityEkus = @(
            $ekuValues | Where-Object {
                $_ -is [string] -and
                $_.StartsWith("1.3.6.1.4.1.311.97.", [System.StringComparison]::Ordinal) -and
                $_ -ne "1.3.6.1.4.1.311.97.1.0"
            }
        )
        [PSCustomObject]@{
            Path                       = $path
            Status                     = $signature.Status.ToString()
            SignerThumbprint           = if ($null -eq $certificate) { $null } else { $certificate.Thumbprint }
            SignerSubject              = if ($null -eq $certificate) { $null } else { $certificate.Subject }
            SignerIssuer               = if ($null -eq $certificate) { $null } else { $certificate.Issuer }
            SignerRootThumbprint       = Get-AuthenticodeChainRootThumbprint -Certificate $certificate
            SubscriberIdentityEku      = if ($subscriberIdentityEkus.Count -eq 1) { $subscriberIdentityEkus[0] } else { $null }
            SubscriberIdentityEkuCount = $subscriberIdentityEkus.Count
            HasCodeSigningEku          = $ekuValues -contains "1.3.6.1.5.5.7.3.3"
            HasPublicTrustMarker       = $ekuValues -contains "1.3.6.1.4.1.311.97.1.0"
            HasTimestamp               = $null -ne $signature.TimeStamperCertificate
        }
    }
}

function Assert-AuthenticodeReleasePolicy {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Artifacts,

        [switch]$RequireUnsigned,

        [AllowNull()]
        [string]$ExpectedThumbprint,

        [AllowNull()]
        [string]$ExpectedSubscriberIdentityEku,

        [AllowNull()]
        [string]$ExpectedSignerRootThumbprint
    )

    if ($Artifacts.Count -eq 0) {
        throw "At least one release executable is required."
    }
    $normalizedExpected = ConvertTo-NormalizedAuthenticodeThumbprint $ExpectedThumbprint
    $normalizedExpectedSubscriberIdentityEku =
        ConvertTo-NormalizedAuthenticodeSubscriberIdentityEku $ExpectedSubscriberIdentityEku
    $normalizedExpectedRoot = ConvertTo-NormalizedAuthenticodeThumbprint $ExpectedSignerRootThumbprint
    $hasAnyStableIdentity =
        $null -ne $normalizedExpectedSubscriberIdentityEku -or
        $null -ne $normalizedExpectedRoot
    $hasCompleteStableIdentity =
        $null -ne $normalizedExpectedSubscriberIdentityEku -and
        $null -ne $normalizedExpectedRoot
    if ($RequireUnsigned) {
        if ($null -ne $normalizedExpected -or $hasAnyStableIdentity) {
            throw "Unsigned release mode cannot use an expected publisher identity."
        }
        $signedArtifact = $Artifacts |
            Where-Object { $_.Status -ne "NotSigned" } |
            Select-Object -First 1
        if ($null -ne $signedArtifact) {
            throw "Unsigned release mode found an Authenticode signature on $($signedArtifact.Path)."
        }
        return
    }
    if ($null -ne $normalizedExpected -and $hasAnyStableIdentity) {
        throw "Signed release verification cannot combine a leaf thumbprint with a durable Artifact Signing identity."
    }
    if ($null -eq $normalizedExpected -and -not $hasCompleteStableIdentity) {
        if ($hasAnyStableIdentity) {
            throw "Stable Artifact Signing identity requires the subscriber identity EKU and root thumbprint."
        }
        throw "Signed release verification requires the expected publisher thumbprint or complete durable Artifact Signing identity."
    }

    foreach ($artifact in $Artifacts) {
        if ($artifact.Status -ne "Valid" -or
            [string]::IsNullOrWhiteSpace($artifact.SignerThumbprint)) {
            throw "Authenticode verification failed for $($artifact.Path): $($artifact.Status)"
        }
        if (-not $artifact.HasTimestamp) {
            throw "Authenticode signature is missing its RFC 3161 timestamp for $($artifact.Path)."
        }
        if ($null -ne $normalizedExpected) {
            $actualThumbprint = ConvertTo-NormalizedAuthenticodeThumbprint $artifact.SignerThumbprint
            if ($actualThumbprint -ne $normalizedExpected) {
                throw "Authenticode signer thumbprint did not match the configured publisher for $($artifact.Path)."
            }
            continue
        }

        $actualRoot =
            if ($artifact.PSObject.Properties.Name -contains "SignerRootThumbprint") {
                ConvertTo-NormalizedAuthenticodeThumbprint $artifact.SignerRootThumbprint
            } else {
                $null
            }
        $actualSubscriberIdentityEku =
            if ($artifact.PSObject.Properties.Name -contains "SubscriberIdentityEku") {
                ConvertTo-NormalizedAuthenticodeSubscriberIdentityEku $artifact.SubscriberIdentityEku
            } else {
                $null
            }
        $actualSubscriberIdentityEkuCount =
            if ($artifact.PSObject.Properties.Name -contains "SubscriberIdentityEkuCount") {
                [int]$artifact.SubscriberIdentityEkuCount
            } else {
                0
            }
        $hasCodeSigningEku =
            $artifact.PSObject.Properties.Name -contains "HasCodeSigningEku" -and
            [bool]$artifact.HasCodeSigningEku
        $hasPublicTrustMarker =
            $artifact.PSObject.Properties.Name -contains "HasPublicTrustMarker" -and
            [bool]$artifact.HasPublicTrustMarker
        if ($actualSubscriberIdentityEkuCount -ne 1 -or
            $null -eq $actualSubscriberIdentityEku -or
            -not $hasCodeSigningEku -or
            -not $hasPublicTrustMarker -or
            $null -eq $actualRoot) {
            throw "Authenticode signature is missing its durable Artifact Signing identity, required EKUs, or certificate chain for $($artifact.Path)."
        }
        if ($actualSubscriberIdentityEku -ne $normalizedExpectedSubscriberIdentityEku -or
            $actualRoot -ne $normalizedExpectedRoot) {
            throw "Authenticode durable publisher identity or certificate chain did not match the configured publisher for $($artifact.Path)."
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
    ConvertTo-NormalizedAuthenticodeSubscriberIdentityEku, `
    ConvertTo-NormalizedAuthenticodeThumbprint, `
    Get-AuthenticodePolicyArtifacts, `
    Invoke-SignToolVerification
