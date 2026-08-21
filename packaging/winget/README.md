# WinGet submission handoff

WinGet's community repository does not accept PowerShell scripts or singleton
manifests as installers. Codex Continuity therefore publishes a versioned
`CodexContinuity-vX.Y.Z-Setup.exe` and generates the required version,
default-locale, and installer manifests from that release asset's actual
SHA-256.

The release workflow validates and attaches
`CodexContinuity-vX.Y.Z-winget-manifests.zip`. Review those files, verify the
release's signature/provenance state, then use `wingetcreate submit` or open a
PR against `microsoft/winget-pkgs`. Submission is intentionally not automatic.

To reproduce a manifest set:

```powershell
.\scripts\write-winget-manifests.ps1 `
  -Version 0.2.1 `
  -InstallerUrl https://github.com/YesterdaysLemon/codex-continuity/releases/download/v0.2.1/CodexContinuity-v0.2.1-Setup.exe `
  -InstallerSha256 <64-character-release-sha256> `
  -ReleaseDate 2026-08-21 `
  -OutputDirectory .\artifacts\winget
winget validate --manifest .\artifacts\winget --disable-interactivity
```
