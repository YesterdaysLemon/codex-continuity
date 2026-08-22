# Windows release signing

Codex Continuity intentionally publishes unsigned artifacts until a production
Authenticode identity is configured. This preserves the updater's fail-closed
publisher check: an unsigned installed build cannot automatically stage another
unsigned build.

## Repository policy

The release workflow has two mutually exclusive modes:

- With no signing configuration, every supervisor and tray executable must be
  unsigned. A partially or unexpectedly signed artifact fails the release.
- If any signing setting is present, all three settings are required. Every
  executable must have a valid Authenticode signature, an RFC 3161 timestamp,
  the configured publisher-certificate thumbprint, and the same signer as every
  other executable.

All published executables, archives, checksum files, WinGet manifest bundles,
and the bootstrap `install.ps1` are included in GitHub build provenance for
future releases produced by this workflow.

## External publisher gate

Do not use a self-signed test certificate for a public release. The repository
owner must first acquire a Windows code-signing certificate for the intended
publisher identity and confirm the certificate's code-signing usage and export
policy. Then configure:

| GitHub setting | Kind | Value |
| --- | --- | --- |
| `WINDOWS_SIGNING_CERTIFICATE_BASE64` | Actions secret | Base64-encoded production PFX |
| `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` | Actions secret | PFX password |
| `WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT` | Actions variable | The certificate's 40-hex SHA-1 thumbprint |

The thumbprint is a public identifier, not a credential. The PFX and password
must remain in Actions secrets and must never be committed, pasted into an
issue, or printed in workflow output.

After all three settings are present, publish a new version through the normal
green-`main` release path. Verify the downloaded Setup, supervisor, and tray
executables with `Get-AuthenticodeSignature`, and verify provenance with:

```powershell
gh attestation verify .\CodexContinuity-Setup.exe --repo YesterdaysLemon/codex-continuity
gh attestation verify .\install.ps1 --repo YesterdaysLemon/codex-continuity
```

Do not move an existing tag or retrofit files into an old release. If signing
configuration or timestamping fails, fix the configuration and publish a new
version after its complete CI run passes.
