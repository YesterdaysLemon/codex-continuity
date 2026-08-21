[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Paths,

    [switch]$VerifyOnly,

    [switch]$AllowUnsigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedPaths = @($Paths | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
if ($resolvedPaths.Count -eq 0) {
    throw "At least one release executable is required."
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
        $certificateBase64 = $env:CONTINUITY_SIGNING_CERTIFICATE_BASE64
        $certificatePassword = $env:CONTINUITY_SIGNING_CERTIFICATE_PASSWORD
        if ([string]::IsNullOrWhiteSpace($certificateBase64) -or
            [string]::IsNullOrWhiteSpace($certificatePassword)) {
            throw "Signing is enabled but the certificate or password secret is missing."
        }

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

    $expectedThumbprint = $null
    foreach ($path in $resolvedPaths) {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned -and
            $AllowUnsigned) {
            Write-Warning "Unsigned development artifact: $path"
            continue
        }
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $null -eq $signature.SignerCertificate) {
            throw "Authenticode verification failed for ${path}: $($signature.Status)"
        }
        $thumbprint = $signature.SignerCertificate.Thumbprint
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
