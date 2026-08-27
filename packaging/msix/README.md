# Microsoft Store package prototype

This directory is a deliberately non-shippable MSIX architecture prototype.
It proves the full-trust process layout, package-identity update boundary, and
disabled-by-default supervisor startup-task declaration without pretending
that the remaining lifecycle gate is solved.

Do not submit or distribute a package from this template yet. Codex currently
reads `CODEX_APP_SERVER_WS_URL` from a machine-visible user environment value.
MSIX registry virtualization would hide that value from separately packaged
Codex; disabling virtualization would make it visible but would not clean it
up on package uninstall. MSIX also has no general custom-uninstall hook. A
removed package could therefore leave Codex pointing at a dead supervisor.

Run the executable preflight for the machine-readable boundary:

```powershell
dotnet run --project .\CodexContinuity.csproj -- store-readiness
```

It exits `2` while any submission gate is blocked. The prototype staging
script uses the same rule and refuses to produce a package:

```powershell
.\scripts\stage-store-prototype.ps1 `
  -IdentityName '<Partner Center identity name>' `
  -Publisher '<Partner Center publisher ID>' `
  -PublisherDisplayName '<Partner Center display name>'
```

The staged directory contains `DO-NOT-SUBMIT.txt` and intentionally is not
passed to MakeAppx. Once a supported reversible endpoint seam exists, the next
step is to turn the two blocked endpoint gates green, add a signed two-version
install/update/uninstall experiment, and only then enable package creation.

Package identity does not by itself prove that Microsoft Store installed or
updates a build; sideloading and enterprise managers can also provide identity.
The runtime therefore rejects Continuity's GitHub update/activation commands
for every packaged build while describing source ownership as unverified. A
future Store artifact must add evidence for its actual update source. The
existing direct EXE installer and updater are unchanged.

The tray executable is included for layout validation but has no startup task
yet. Adding a second startup task before the supervisor lifecycle is proven
would widen the Store surface without helping the central continuity claim.
