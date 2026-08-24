[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Paths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$signingScript = Join-Path $PSScriptRoot "sign-release.ps1"
$policyModule = Join-Path $PSScriptRoot "authenticode-release-policy.psm1"
Import-Module $policyModule -Force
$trustedThumbprint = "A" * 40

function Assert-FailsWith {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedMessage
    )

    try {
        & $Command
    }
    catch {
        if ($_.Exception.Message -notmatch $ExpectedMessage) {
            throw "Expected failure matching '$ExpectedMessage', got: $($_.Exception.Message)"
        }
        $global:LASTEXITCODE = 0
        return
    }
    throw "Expected command to fail with '$ExpectedMessage'."
}

& $signingScript -Paths $Paths -VerifyOnly -RequireUnsigned
Assert-FailsWith {
    & $signingScript -Paths $Paths -VerifyOnly -ExpectedThumbprint $trustedThumbprint
} "Authenticode verification failed"
Assert-FailsWith {
    & $signingScript -Paths $Paths -VerifyOnly -ExpectedThumbprint "not-a-thumbprint"
} "exactly 40 hexadecimal characters"

$validArtifacts = @(
    [PSCustomObject]@{
        Path = "supervisor.exe"
        Status = "Valid"
        SignerThumbprint = $trustedThumbprint
        HasTimestamp = $true
    },
    [PSCustomObject]@{
        Path = "tray.exe"
        Status = "Valid"
        SignerThumbprint = $trustedThumbprint.ToLowerInvariant()
        HasTimestamp = $true
    }
)
Assert-AuthenticodeReleasePolicy `
    -Artifacts $validArtifacts `
    -ExpectedThumbprint ("AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA AA")
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts @($validArtifacts[0], [PSCustomObject]@{
                Path = "unsigned.exe"
                Status = "NotSigned"
                SignerThumbprint = $null
                HasTimestamp = $false
            }) `
        -ExpectedThumbprint $trustedThumbprint
} "Authenticode verification failed for unsigned.exe"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts @([PSCustomObject]@{
                Path = "untimestamped.exe"
                Status = "Valid"
                SignerThumbprint = $trustedThumbprint
                HasTimestamp = $false
            }) `
        -ExpectedThumbprint $trustedThumbprint
} "missing its RFC 3161 timestamp"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts @($validArtifacts[0], [PSCustomObject]@{
                Path = "other-publisher.exe"
                Status = "Valid"
                SignerThumbprint = "B" * 40
                HasTimestamp = $true
            }) `
        -ExpectedThumbprint $trustedThumbprint
} "did not match the configured publisher"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts $validArtifacts `
        -RequireUnsigned
} "Unsigned release mode found an Authenticode signature"

$successTool = Join-Path ([IO.Path]::GetTempPath()) (
    "codex-continuity-signtool-success-" + [Guid]::NewGuid().ToString("N") + ".cmd")
$failureTool = Join-Path ([IO.Path]::GetTempPath()) (
    "codex-continuity-signtool-failure-" + [Guid]::NewGuid().ToString("N") + ".cmd")
try {
    "@exit /b 0" | Set-Content -LiteralPath $successTool -Encoding ascii
    "@exit /b 23" | Set-Content -LiteralPath $failureTool -Encoding ascii
    Invoke-SignToolVerification -SignToolPath $successTool -Path "fixture.exe"
    Assert-FailsWith {
        Invoke-SignToolVerification -SignToolPath $failureTool -Path "fixture.exe"
    } "SignTool verification failed"
}
finally {
    Remove-Item -LiteralPath $successTool, $failureTool -Force -ErrorAction SilentlyContinue
}

$previousCertificate = $env:CONTINUITY_SIGNING_CERTIFICATE_BASE64
$previousPassword = $env:CONTINUITY_SIGNING_CERTIFICATE_PASSWORD
$previousThumbprint = $env:CONTINUITY_SIGNING_EXPECTED_THUMBPRINT
$previousSubscriberIdentityEku = $env:CONTINUITY_SIGNING_EXPECTED_SUBSCRIBER_IDENTITY_EKU
$previousRootThumbprint = $env:CONTINUITY_SIGNING_EXPECTED_ROOT_THUMBPRINT
try {
    $partialConfigurations = @(
        @{
            Certificate = $null
            Password = "configured-password"
            Thumbprint = $trustedThumbprint
            Missing = "certificate"
        },
        @{
            Certificate = "configured-certificate"
            Password = $null
            Thumbprint = $trustedThumbprint
            Missing = "password"
        },
        @{
            Certificate = "configured-certificate"
            Password = "configured-password"
            Thumbprint = $null
            Missing = "expected thumbprint"
        }
    )
    foreach ($configuration in $partialConfigurations) {
        $env:CONTINUITY_SIGNING_CERTIFICATE_BASE64 = $configuration.Certificate
        $env:CONTINUITY_SIGNING_CERTIFICATE_PASSWORD = $configuration.Password
        $env:CONTINUITY_SIGNING_EXPECTED_THUMBPRINT = $configuration.Thumbprint
        $env:CONTINUITY_SIGNING_EXPECTED_SUBSCRIBER_IDENTITY_EKU = $null
        $env:CONTINUITY_SIGNING_EXPECTED_ROOT_THUMBPRINT = $null
        Assert-FailsWith {
            & $signingScript -Paths $Paths
        } "Signing configuration is incomplete.*$($configuration.Missing)"
    }
}
finally {
    $env:CONTINUITY_SIGNING_CERTIFICATE_BASE64 = $previousCertificate
    $env:CONTINUITY_SIGNING_CERTIFICATE_PASSWORD = $previousPassword
    $env:CONTINUITY_SIGNING_EXPECTED_THUMBPRINT = $previousThumbprint
    $env:CONTINUITY_SIGNING_EXPECTED_SUBSCRIBER_IDENTITY_EKU = $previousSubscriberIdentityEku
    $env:CONTINUITY_SIGNING_EXPECTED_ROOT_THUMBPRINT = $previousRootThumbprint
}

$stableSubscriberIdentityEku = "1.3.6.1.4.1.311.97.990309390.766961637.194916062.941502583"
$differentSubscriberIdentityEku = "1.3.6.1.4.1.311.97.990309390.766961637.194916062.941502584"
$stableRoot = "D" * 40
$rotatedArtifacts = @(
    [PSCustomObject]@{
        Path                       = "supervisor.exe"
        Status                     = "Valid"
        SignerThumbprint           = "A" * 40
        SignerSubject              = "CN=YesterdaysLemon, O=YesterdaysLemon"
        SignerIssuer               = "CN=Microsoft Identity Verification Root Certificate Authority 2020"
        SignerRootThumbprint       = $stableRoot
        SubscriberIdentityEku      = $stableSubscriberIdentityEku
        SubscriberIdentityEkuCount = 1
        HasCodeSigningEku          = $true
        HasPublicTrustMarker       = $true
        HasTimestamp               = $true
    },
    [PSCustomObject]@{
        Path                       = "tray.exe"
        Status                     = "Valid"
        SignerThumbprint           = "B" * 40
        SignerSubject              = "CN=YesterdaysLemon Renewed, O=YesterdaysLemon"
        SignerIssuer               = "CN=Microsoft Identity Verification Intermediate 2022"
        SignerRootThumbprint       = $stableRoot.ToLowerInvariant()
        SubscriberIdentityEku      = $stableSubscriberIdentityEku
        SubscriberIdentityEkuCount = 1
        HasCodeSigningEku          = $true
        HasPublicTrustMarker       = $true
        HasTimestamp               = $true
    }
)
Assert-AuthenticodeReleasePolicy `
    -Artifacts $rotatedArtifacts `
    -ExpectedSubscriberIdentityEku $stableSubscriberIdentityEku `
    -ExpectedSignerRootThumbprint $stableRoot
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts $rotatedArtifacts `
        -ExpectedSubscriberIdentityEku $stableSubscriberIdentityEku
} "requires the subscriber identity EKU and root thumbprint"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts $rotatedArtifacts `
        -ExpectedSubscriberIdentityEku $stableSubscriberIdentityEku `
        -ExpectedSignerRootThumbprint "not-a-thumbprint"
} "exactly 40 hexadecimal characters"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts $rotatedArtifacts `
        -ExpectedSubscriberIdentityEku "1.3.6.1.4.1.311.97.1.0" `
        -ExpectedSignerRootThumbprint $stableRoot
} "subscriber identity EKU"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts @($rotatedArtifacts[0], [PSCustomObject]@{
                Path                       = "different-root.exe"
                Status                     = "Valid"
                SignerThumbprint           = "C" * 40
                SignerSubject              = "CN=YesterdaysLemon"
                SignerIssuer               = "CN=Different Intermediate"
                SignerRootThumbprint       = "E" * 40
                SubscriberIdentityEku      = $stableSubscriberIdentityEku
                SubscriberIdentityEkuCount = 1
                HasCodeSigningEku          = $true
                HasPublicTrustMarker       = $true
                HasTimestamp               = $true
            }) `
        -ExpectedSubscriberIdentityEku $stableSubscriberIdentityEku `
        -ExpectedSignerRootThumbprint $stableRoot
} "durable publisher identity or certificate chain did not match"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts @([PSCustomObject]@{
                Path                       = "different-identity.exe"
                Status                     = "Valid"
                SignerThumbprint           = "C" * 40
                SignerSubject              = "CN=YesterdaysLemon, O=YesterdaysLemon"
                SignerIssuer               = "CN=Microsoft Identity Verification Root Certificate Authority 2020"
                SignerRootThumbprint       = $stableRoot
                SubscriberIdentityEku      = $differentSubscriberIdentityEku
                SubscriberIdentityEkuCount = 1
                HasCodeSigningEku          = $true
                HasPublicTrustMarker       = $true
                HasTimestamp               = $true
            }) `
        -ExpectedSubscriberIdentityEku $stableSubscriberIdentityEku `
        -ExpectedSignerRootThumbprint $stableRoot
} "durable publisher identity or certificate chain did not match"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts @([PSCustomObject]@{
                Path                       = "missing-code-signing-eku.exe"
                Status                     = "Valid"
                SignerThumbprint           = "C" * 40
                SignerRootThumbprint       = $stableRoot
                SubscriberIdentityEku      = $stableSubscriberIdentityEku
                SubscriberIdentityEkuCount = 1
                HasCodeSigningEku          = $false
                HasPublicTrustMarker       = $true
                HasTimestamp               = $true
            }) `
        -ExpectedSubscriberIdentityEku $stableSubscriberIdentityEku `
        -ExpectedSignerRootThumbprint $stableRoot
} "missing its durable Artifact Signing identity, required EKUs, or certificate chain"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts @([PSCustomObject]@{
                Path                       = "missing-public-trust-marker.exe"
                Status                     = "Valid"
                SignerThumbprint           = "C" * 40
                SignerRootThumbprint       = $stableRoot
                SubscriberIdentityEku      = $stableSubscriberIdentityEku
                SubscriberIdentityEkuCount = 1
                HasCodeSigningEku          = $true
                HasPublicTrustMarker       = $false
                HasTimestamp               = $true
            }) `
        -ExpectedSubscriberIdentityEku $stableSubscriberIdentityEku `
        -ExpectedSignerRootThumbprint $stableRoot
} "missing its durable Artifact Signing identity, required EKUs, or certificate chain"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts @([PSCustomObject]@{
                Path                       = "ambiguous.exe"
                Status                     = "Valid"
                SignerThumbprint           = "C" * 40
                SignerRootThumbprint       = $stableRoot
                SubscriberIdentityEku      = $stableSubscriberIdentityEku
                SubscriberIdentityEkuCount = 2
                HasCodeSigningEku          = $true
                HasPublicTrustMarker       = $true
                HasTimestamp               = $true
            }) `
        -ExpectedSubscriberIdentityEku $stableSubscriberIdentityEku `
        -ExpectedSignerRootThumbprint $stableRoot
} "missing its durable Artifact Signing identity, required EKUs, or certificate chain"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts @([PSCustomObject]@{
                Path                       = "malformed-identity.exe"
                Status                     = "Valid"
                SignerThumbprint           = "C" * 40
                SignerRootThumbprint       = $stableRoot
                SubscriberIdentityEku      = "not-an-oid"
                SubscriberIdentityEkuCount = 1
                HasCodeSigningEku          = $true
                HasPublicTrustMarker       = $true
                HasTimestamp               = $true
            }) `
        -ExpectedSubscriberIdentityEku $stableSubscriberIdentityEku `
        -ExpectedSignerRootThumbprint $stableRoot
} "subscriber identity EKU"
Assert-FailsWith {
    ConvertTo-NormalizedAuthenticodeSubscriberIdentityEku "$stableSubscriberIdentityEku`n.7"
} "complete dotted numeric OID"
Assert-FailsWith {
    Assert-AuthenticodeReleasePolicy `
        -Artifacts $rotatedArtifacts `
        -ExpectedThumbprint ("A" * 40) `
        -ExpectedSubscriberIdentityEku $stableSubscriberIdentityEku `
        -ExpectedSignerRootThumbprint $stableRoot
} "cannot combine a leaf thumbprint"

Write-Host "Signing policy tests passed."
