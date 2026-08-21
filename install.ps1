[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",

    [string]$Version = "latest",

    [ValidateRange(1, 65535)]
    [int]$Port = 45123,

    [switch]$StartNow,

    [switch]$SkipSelfTest,

    [switch]$NoTray,

    [switch]$Json,

    [switch]$Plan,

    [string]$DownloadBaseUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Result {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Value
    )

    if ($Json) {
        $Value | ConvertTo-Json -Compress -Depth 6
        return
    }

    foreach ($entry in $Value.GetEnumerator()) {
        Write-Host ("{0}: {1}" -f $entry.Key, $entry.Value)
    }
}

function Resolve-ReleaseBaseUrl {
    if (-not [string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
        return $DownloadBaseUrl.TrimEnd("/")
    }

    if ($Version -eq "latest") {
        return "https://github.com/YesterdaysLemon/codex-continuity/releases/latest/download"
    }

    $tag = $Version
    if (-not $tag.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $tag = "v$tag"
    }
    return "https://github.com/YesterdaysLemon/codex-continuity/releases/download/$tag"
}

function Remove-VerifiedTemporaryDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $leaf = Split-Path -Leaf $fullPath
    if (-not $fullPath.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith("codex-continuity-install-", [System.StringComparison]::Ordinal)) {
        throw "Refusing to remove unexpected temporary path: $fullPath"
    }

    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

$assetName = "CodexContinuity-$Runtime.zip"
$checksumName = "$assetName.sha256"
$releaseBaseUrl = Resolve-ReleaseBaseUrl
$assetUrl = "$releaseBaseUrl/$assetName"
$checksumUrl = "$releaseBaseUrl/$checksumName"

if ($Plan) {
    Write-Result @{
        mode = "plan"
        version = $Version
        runtime = $Runtime
        port = $Port
        assetUrl = $assetUrl
        checksumUrl = $checksumUrl
        selfTest = -not $SkipSelfTest
        tray = -not $NoTray
        startNow = [bool]$StartNow
        restartsCodex = $false
    }
    return
}

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "codex-continuity-install-" + [System.Guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $workRoot $assetName
$checksumPath = Join-Path $workRoot $checksumName
$extractPath = Join-Path $workRoot "extracted"

try {
    New-Item -ItemType Directory -Path $workRoot | Out-Null
    Invoke-WebRequest -Uri $assetUrl -OutFile $archivePath -UseBasicParsing
    Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumPath -UseBasicParsing

    $checksumText = Get-Content -LiteralPath $checksumPath -Raw
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $checksumText,
        "(?im)^([0-9a-f]{64})(?:\s+|$)")
    if (-not $match.Success) {
        throw "The published checksum file does not contain a SHA-256 digest."
    }

    $expectedHash = $match.Groups[1].Value.ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "SHA-256 mismatch. Expected $expectedHash but downloaded $actualHash."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath
    $executables = @(Get-ChildItem -LiteralPath $extractPath -Filter CodexContinuity.exe -Recurse)
    if ($executables.Count -ne 1) {
        throw "Expected exactly one CodexContinuity.exe in the release archive; found $($executables.Count)."
    }
    $executable = $executables[0].FullName

    $selfTestPassed = $false
    if (-not $SkipSelfTest) {
        $selfTestOutput = @(& $executable self-test 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "The isolated reconnect self-test failed:`n$($selfTestOutput -join [Environment]::NewLine)"
        }
        $selfTestPassed = $true
        if (-not $Json) {
            $selfTestOutput | Write-Host
        }
    }

    $installArguments = @("install", "--port", $Port)
    if ($NoTray) {
        $installArguments += "--no-tray"
    }
    if ($StartNow) {
        $installArguments += "--start-now"
    }
    $installOutput = @(& $executable @installArguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Codex Continuity installation failed:`n$($installOutput -join [Environment]::NewLine)"
    }
    if (-not $Json) {
        $installOutput | Write-Host
    }

    Write-Result @{
        installed = $true
        version = $Version
        runtime = $Runtime
        sha256 = $actualHash
        selfTestPassed = $selfTestPassed
        startNow = [bool]$StartNow
        restartsCodex = $false
    }
}
finally {
    Remove-VerifiedTemporaryDirectory -Path $workRoot
}
