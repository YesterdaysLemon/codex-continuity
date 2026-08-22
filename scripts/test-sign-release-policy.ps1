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
        Assert-FailsWith {
            & $signingScript -Paths $Paths
        } "Signing configuration is incomplete.*$($configuration.Missing)"
    }
}
finally {
    $env:CONTINUITY_SIGNING_CERTIFICATE_BASE64 = $previousCertificate
    $env:CONTINUITY_SIGNING_CERTIFICATE_PASSWORD = $previousPassword
    $env:CONTINUITY_SIGNING_EXPECTED_THUMBPRINT = $previousThumbprint
}

Write-Host "Signing policy tests passed."
