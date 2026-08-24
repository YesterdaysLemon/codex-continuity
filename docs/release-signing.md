# Windows release signing

Codex Continuity intentionally publishes unsigned artifacts until a production
Authenticode identity is configured. This preserves the updater's fail-closed
publisher check: an unsigned installed build cannot automatically stage another
unsigned build.

## Repository policy

The release workflow has three explicit modes selected by the repository
variable `WINDOWS_SIGNING_MODE`:

- `unsigned` (the default when the variable is empty): every supervisor and
  tray executable must be unsigned. Any partial signing configuration or
  unexpected signature fails the release.
- `artifact-signing`: Microsoft Azure Artifact Signing signs both executables
  through GitHub OIDC. This is the recommended public-release path.
- `pfx`: the legacy SignTool/PFX path. Keep this only when the certificate
  provider explicitly supplies a compliant signing integration that permits
  this workflow. Do not export a modern hardware-backed private key merely to
  make it fit this mode.

Every signed executable must have a valid Authenticode signature, an RFC 3161
timestamp, and the configured publisher identity. ZIP files, checksum files,
WinGet manifests, and `install.ps1` are not Authenticode-signed; they are
covered by SHA-256 files and GitHub build provenance. The setup executable is a
byte-for-byte copy of the signed supervisor executable.

## Recommended path: Azure Artifact Signing

Microsoft now calls the service formerly known as Trusted Signing **Azure
Artifact Signing**. Microsoft documents it as the recommended option for
non-Store distribution. Basic currently costs about $9.99/month and includes
5,000 signatures/month. It uses short-lived certificates and managed HSMs, so
the release workflow must identify the stable verified publisher identity and
certificate chain rather than pinning one leaf certificate thumbprint.

The owner must complete the external identity gate before enabling this mode:

1. Create an Azure subscription and Artifact Signing account.
2. Complete Public Trust identity validation in the Azure portal. Use the exact
   legal/billing identity that should appear on the certificate; validation can
   take 1–20 business days and may request documents. Individual public-trust
   developers are currently limited to the US and Canada; organization
   eligibility is region-dependent.
3. Create a Public Trust certificate profile and assign the
   `Artifact Signing Certificate Profile Signer` role to the identity used by
   the workflow.
4. Create an Entra application or user-assigned managed identity and a GitHub
   OIDC federated credential restricted to this repository and the production
   release ref/environment. No Azure client secret is required.
5. After a controlled signed test, record the complete subscriber-identity EKU
   and root-chain SHA-1 thumbprint in the repository variables below. Do not
   pin the leaf certificate thumbprint or subject DN: Artifact Signing renews
   certificates daily and Microsoft defines the subscriber-identity EKU as the
   durable identity.
6. Set `WINDOWS_SIGNING_MODE=artifact-signing` only after all required values
   are present, then publish a new version through the normal green-`main`
   release path.

Azure settings used by the workflow:

| GitHub setting | Kind | Value |
| --- | --- | --- |
| `WINDOWS_SIGNING_MODE` | Repository variable | `artifact-signing` |
| `WINDOWS_ARTIFACT_SIGNING_ENDPOINT` | Repository variable | Region-specific `https://<region>.codesigning.azure.net/` endpoint |
| `WINDOWS_ARTIFACT_SIGNING_ACCOUNT_NAME` | Repository variable | Artifact Signing account name |
| `WINDOWS_ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME` | Repository variable | Public Trust certificate profile name |
| `WINDOWS_SIGNING_AZURE_CLIENT_ID` | Actions secret | Entra application or managed-identity client ID |
| `WINDOWS_SIGNING_AZURE_TENANT_ID` | Actions secret | Entra directory/tenant ID |
| `WINDOWS_SIGNING_AZURE_SUBSCRIPTION_ID` | Actions secret | Azure subscription ID |
| `WINDOWS_SIGNING_EXPECTED_SUBSCRIBER_IDENTITY_EKU` | Repository variable | Complete `1.3.6.1.4.1.311.97.*` subscriber-identity EKU |
| `WINDOWS_SIGNING_EXPECTED_ROOT_THUMBPRINT` | Repository variable | 40-hex SHA-1 thumbprint of the trusted chain root |

The two expected identity values are public certificate metadata, not
credentials. Both are required. The certificate must also contain the normal
Code Signing EKU `1.3.6.1.5.5.7.3.3` and the Artifact Signing Public Trust
marker EKU `1.3.6.1.4.1.311.97.1.0`. A missing, malformed, partial, or
ambiguous value fails verification. The updater accepts a rotated leaf
certificate when the durable subscriber-identity EKU and trusted root-chain
identity remain identical, even if subject or issuer text changes.

To collect these values, sign a disposable test executable with the same
Public Trust certificate profile, download it to a trusted Windows machine,
and run:

```powershell
Import-Module .\scripts\authenticode-release-policy.psm1 -Force
$identity = Get-AuthenticodePolicyArtifacts -Paths .\signed-probe.exe
$identity | Format-List Status,HasTimestamp,SubscriberIdentityEku,SubscriberIdentityEkuCount,HasCodeSigningEku,HasPublicTrustMarker,SignerRootThumbprint
```

Confirm `Status` is `Valid`, `HasTimestamp` is `True`,
`SubscriberIdentityEkuCount` is `1`, and both EKU flags are `True`, then copy
only `SubscriberIdentityEku` and `SignerRootThumbprint` into repository
variables. Subject and issuer are diagnostic fields only. Never copy signing
credentials or private-key material from the signing service.

### OIDC trust scope

The continuous-release caller runs from the protected `main` branch, so its
default Azure federated-credential subject is exactly:

```text
repo:YesterdaysLemon/codex-continuity:ref:refs/heads/main
```

Scope the Entra federated credential to that exact subject and the default
Azure audience `api://AzureADTokenExchange`; do not use only
`repo:YesterdaysLemon/codex-continuity`. A reusable workflow token describes the
calling workflow in `sub`; `job_workflow_ref` identifies the called workflow as
a separate claim. A standard Entra FIC that matches only `sub` cannot bind the
called workflow file. If Entra flexible federated credentials are available,
use this claims-matching expression instead (replace the two numeric IDs with
the immutable values from a real GitHub OIDC token):

```text
claims['sub'] eq 'repo:YesterdaysLemon/codex-continuity:ref:refs/heads/main' and claims['job_workflow_ref'] eq 'YesterdaysLemon/codex-continuity/.github/workflows/release.yml@refs/heads/main' and claims['repository_id'] eq '<immutable-repository-id>' and claims['repository_owner_id'] eq '<immutable-owner-id>'
```

Direct `push` runs from tags use a tag subject instead and are not covered by
the `main` subject. Either restrict direct tags separately with protected tag
rules and an exact credential, or use only the continuous release path. If
GitHub repository or organization administrators have enabled an
immutable/custom subject template, use the actual configured subject from a
test token instead of these default-format examples.

If a protected GitHub environment is preferred, add an environment such as
`production-release` to the release job and configure the exact subject
`repo:YesterdaysLemon/codex-continuity:environment:production-release`, with
required reviewers. An environment subject does not work until the workflow
actually references that environment.

The release workflow uses the official `azure/login@v3` and
`azure/artifact-signing-action@v2` actions, SHA-256 file digests, and the
Microsoft RFC 3161 timestamp service at
`http://timestamp.acs.microsoft.com`.

## Legacy PFX mode

Set `WINDOWS_SIGNING_MODE=pfx` only when all three existing settings are
configured:

| GitHub setting | Kind | Value |
| --- | --- | --- |
| `WINDOWS_SIGNING_CERTIFICATE_BASE64` | Actions secret | Base64-encoded production PFX |
| `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` | Actions secret | PFX password |
| `WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT` | Repository variable | Certificate's 40-hex SHA-1 leaf thumbprint |

The script signs with SHA-256 and an RFC 3161 timestamp via
`http://timestamp.digicert.com`, then removes its temporary PFX file. Partial
configuration, an unexpected signer, a missing timestamp, or a mixed
signed/unsigned release fails publication.

Current public code-signing certificates generally require the private key to
remain in a compliant hardware token, cloud HSM, or signing service. A
GitHub-hosted runner cannot use a physical USB token, and an exportable PFX is
not a safe default. Prefer a provider's cloud/KSP integration or a secured
self-hosted runner when a traditional OV/EV certificate is chosen.

## SmartScreen and certificate rotation

Signing does not provide instant SmartScreen trust. New publishers must build
reputation through consistent, clean releases; EV no longer provides the old
first-download bypass. A timestamp preserves the validity of a signature after
the signing certificate expires, but it does not make an invalid or untrusted
publisher acceptable.

The installed updater verifies every candidate with Windows Authenticode. For
an Artifact Signing-installed build, it requires the same durable subscriber-
identity EKU, Code Signing/Public Trust EKUs, and trusted chain root; subject
and issuer text are not trust anchors and may rotate. For a legacy PFX-installed
build, it retains the exact leaf thumbprint and trusted root, so a legacy leaf
rotation is manual rather than silently weakening the check. Unsigned files,
invalid signatures, missing certificate-chain data, different publishers, and
ambiguous identities are rejected.

## Owner security boundary

The repository owner must acquire/validate the signing identity, pay the Azure
or CA service, configure Azure and GitHub settings, keep production secrets in
GitHub Actions secrets, and approve the first signed release. Codex can
modify the workflow, scripts, tests, and documentation, but cannot perform
identity validation, access Azure, enter secrets, or approve a production
release.

Never commit or paste a PFX, private-key password, client secret, or token into
the repository, an issue, or workflow output. A protected GitHub environment
with required reviewers is recommended defense in depth when its release delay
fits the project; the OIDC credential must always be scoped to this repository
and its release ref or environment.

The first transition from an unsigned installed build to a signed build is a
manual install: the fail-closed updater cannot treat an unsigned executable as
a publisher trust anchor. Switching an installed PFX-signed publisher to the
Artifact Signing publisher is also a manual install because the durable
identity/chain changes. After the new signed release is installed, later
releases with the same verified durable identity can stage automatically.

After signing a new release, verify the downloaded files and provenance:

```powershell
Get-AuthenticodeSignature .\CodexContinuity-Setup.exe | Format-List *
signtool verify /pa /v .\CodexContinuity-Setup.exe
gh attestation verify .\CodexContinuity-Setup.exe --repo YesterdaysLemon/codex-continuity
gh attestation verify .\install.ps1 --repo YesterdaysLemon/codex-continuity
```

Do not move an existing tag or retrofit files into an old release. If signing,
identity validation, or timestamping fails, fix the configuration and publish a
new version after its complete CI workflow passes.

Official references:

- [Microsoft code-signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)
- [Artifact Signing quickstart](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)
- [Artifact Signing roles and resources](https://learn.microsoft.com/en-us/azure/artifact-signing/concept-resources-roles)
- [Artifact Signing certificate management and durable identity EKU](https://learn.microsoft.com/en-us/azure/artifact-signing/concept-certificate-management)
- [Official Artifact Signing GitHub Action](https://github.com/azure/artifact-signing-action)
- [Artifact Signing Action OIDC setup](https://github.com/azure/artifact-signing-action/blob/main/docs/OIDC.md)
- [Azure/GitHub OIDC](https://learn.microsoft.com/en-us/azure/developer/github/connect-from-azure-openid-connect)
- [Microsoft Entra flexible federated credentials](https://learn.microsoft.com/en-us/entra/workload-id/workload-identities-set-up-flexible-federated-identity-credential)
- [Authenticode timestamping](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures)
- [CA/Browser Forum code-signing requirements](https://cabforum.org/working-groups/code-signing/requirements/)
