# Security

## Supported version

Only the latest release is supported. Codex Continuity depends on experimental
desktop and app-server behavior, so old releases should not be assumed safe or
compatible with newer Codex desktop builds.

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub's **Report a
vulnerability** flow instead of opening a public issue. Include the Codex
desktop version, Codex CLI version, Windows version, reproduction steps, and
whether the issue can expose the loopback app-server beyond the local machine.

## Security model

- The supervised WebSocket binds only to `127.0.0.1`.
- Loopback is a network-exposure boundary, not a same-machine authentication
  boundary. Any local process able to reach the port can attempt the app-server
  protocol. Do not run Continuity on a shared or untrusted Windows host.
- The utility does not proxy or retain prompt content.
- Authentication and thread persistence remain owned by the official Codex
  app-server and the user's existing Codex home.
- The utility does not patch, replace, or resign the installed desktop app.
- Installation changes only user environment variables, a user startup entry,
  and files under the user's local application-data directory.
- App-server stdout and stderr are local diagnostics and may contain paths,
  thread metadata, or error context. Logs are size-bounded and rotated, but
  should still be handled as potentially sensitive user data.

## Release integrity

Release ZIPs and setup executables include SHA-256 checksums and GitHub build
provenance attestations. Verify them before running the binary:

```powershell
Get-FileHash .\CodexContinuity-Setup.exe -Algorithm SHA256
gh attestation verify .\CodexContinuity-Setup.exe --repo YesterdaysLemon/codex-continuity
```

The release workflow defaults to unsigned artifacts and has two opt-in signing
modes: Microsoft Azure Artifact Signing through GitHub OIDC, or the bounded
legacy PFX path. The selected mode, complete publisher identity/chain policy,
RFC 3161/SHA-256 timestamp, and every executable must agree; partial
configuration, an unexpected signer, a missing timestamp, certificate-chain
ambiguity, or a mixed signed/unsigned release fails publication. Artifact
Signing leaf certificates may rotate daily, so the updater checks the durable
subscriber-identity EKU, required Code Signing/Public Trust EKUs, and trusted
chain root rather than one leaf thumbprint or mutable subject text. A legacy
PFX-installed build remains leaf-thumbprint and root pinned; its leaf rotation
or transition to Artifact Signing requires a manual install.

Until a production identity is configured, Windows may show an "Unknown
publisher" or SmartScreen warning. Do not bypass a warning unless the download
URL, SHA-256, and GitHub attestation match the official release. See [the
signing runbook](docs/release-signing.md) for the external identity gate and
the exact owner-supplied Azure/GitHub settings.
