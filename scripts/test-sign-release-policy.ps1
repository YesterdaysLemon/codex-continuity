[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Paths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$signingScript = Join-Path $PSScriptRoot "sign-release.ps1"

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
    & $signingScript -Paths $Paths -VerifyOnly
} "Authenticode verification failed"
Assert-FailsWith {
    & $signingScript -Paths $Paths -VerifyOnly -ExpectedThumbprint "not-a-thumbprint"
} "exactly 40 hexadecimal characters"

$previousCertificate = $env:CONTINUITY_SIGNING_CERTIFICATE_BASE64
$previousPassword = $env:CONTINUITY_SIGNING_CERTIFICATE_PASSWORD
$previousThumbprint = $env:CONTINUITY_SIGNING_EXPECTED_THUMBPRINT
try {
    $env:CONTINUITY_SIGNING_CERTIFICATE_BASE64 = "configured-certificate"
    $env:CONTINUITY_SIGNING_CERTIFICATE_PASSWORD = $null
    $env:CONTINUITY_SIGNING_EXPECTED_THUMBPRINT = $null
    Assert-FailsWith {
        & $signingScript -Paths $Paths
    } "Signing configuration is incomplete.*password, expected thumbprint"
}
finally {
    $env:CONTINUITY_SIGNING_CERTIFICATE_BASE64 = $previousCertificate
    $env:CONTINUITY_SIGNING_CERTIFICATE_PASSWORD = $previousPassword
    $env:CONTINUITY_SIGNING_EXPECTED_THUMBPRINT = $previousThumbprint
}

Write-Host "Signing policy tests passed."
