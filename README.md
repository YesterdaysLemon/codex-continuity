# Codex Continuity

> Keep the agents. Replace the window.

[![CI](https://github.com/YesterdaysLemon/codex-continuity/actions/workflows/ci.yml/badge.svg)](https://github.com/YesterdaysLemon/codex-continuity/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/YesterdaysLemon/codex-continuity)](https://github.com/YesterdaysLemon/codex-continuity/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-79f2c0.svg)](LICENSE)
[![Sponsor](https://img.shields.io/badge/Sponsor-%E2%99%A5-ea4aaa?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/YesterdaysLemon)

Codex Continuity is an **unofficial, experimental Windows utility** that keeps
the Codex agent backend alive independently of the desktop window. If Microsoft
Store replaces or restarts the UI during an update, working threads remain
owned by the supervised backend and the new UI reconnects.

[Download the latest release](https://github.com/YesterdaysLemon/codex-continuity/releases/latest)
· [Visit the product site](https://continuity.alirezaafshan.com)
· [Read the technical evidence](REVERSE_ENGINEERING.md)

## Install in one command

Paste this into PowerShell. The bootstrapper downloads the stable Windows x64
asset, verifies its published SHA-256 checksum, runs the isolated reconnect
self-test, and installs it without restarting Codex:

```powershell
$i="$env:TEMP\codex-continuity-install.ps1"; curl.exe -fsSL https://github.com/YesterdaysLemon/codex-continuity/releases/latest/download/install.ps1 -o $i; powershell.exe -NoProfile -ExecutionPolicy Bypass -File $i -StartNow
```

If Codex is already open, Continuity reserves its endpoint but leaves the
second app-server off. It waits for those exact desktop processes to close in
their own time, then starts the supervised backend for the next natural launch.
It never asks you to restart Codex.

Agents and automation can inspect the exact plan without downloading or
changing anything:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $i -Plan -Json
```

## Installing from an agent

Agents should download `install.ps1`, run `-Plan -Json`, present the resolved
asset/checksum URLs and `restartsCodex: false`, then run with `-StartNow` only
after that plan is accepted. Add `-NoTray` for headless automation. The same
contract is published at the site's [`/llms.txt`](https://continuity.alirezaafshan.com/llms.txt).

The prepared WinGet identity is `YesterdaysLemon.CodexContinuity`. After the
external community-manifest review completes, the standard command will be:

```powershell
winget install --id YesterdaysLemon.CodexContinuity -e
```

## Installed CLI

Installation adds a stable `CodexContinuity` command to the user `PATH`. Open a
new PowerShell window after installation, then inspect or maintain the service
without finding a versioned executable:

```powershell
CodexContinuity status
CodexContinuity probe
CodexContinuity attach
CodexContinuity repair
CodexContinuity uninstall
```

`uninstall` restores only the environment, startup, `PATH`, and Installed Apps
values still owned by Continuity. It never stops the running backend or active
agents. When that backend is still reachable, Codex reopenings in the current
Windows session keep reconnecting to it; the owned reconnect setting, app files,
and logs are removed at the next sign-in, when they are no longer in use.

## Why a separate executable?

A plugin would share the desktop lifecycle and disappear during the same
restart. Codex Continuity runs outside the app, supervises `codex app-server` on
a loopback-only WebSocket, and configures future desktop launches to reconnect
to it.

```text
Codex desktop UI  ── reconnectable WebSocket ──  supervised app-server
      │                                                │
      └──── Store may update/restart this process      └──── owns threads
```

## What it does

- Keeps the app-server in a user-level background supervisor.
- Arms safely during first install when Codex is already open, without starting
  a competing app-server or asking for a relaunch.
- Shows optional health, active-agent count, and update status in the Windows
  notification area; the tray is a separate process and can safely exit.
- Checks for stable Continuity releases at supervisor start and every four
  hours. Automatic staging requires the archive checksum, a valid Authenticode
  signature from the same publisher as the installed build, and a passing
  isolated self-test; it never restarts active agents. Applying a staged build
  remains off until the user explicitly enables safe idle activation.
- Binds only to `127.0.0.1`; it does not expose Codex over the network.
- Removes the desktop's blue in-app update prompt on future launches.
- Leaves signed package delivery to Microsoft Store, Intune, or another
  external package manager.
- Reports backend health and active thread count through `status`.
- Includes an isolated reconnect self-test and a reversible install.

Microsoft Store's **Settings > App updates** option must remain on for the fully
automatic path. Store-delivered MSIX packages are updated by Windows in the
background rather than by this tool.

Continuity's own updater is separate from those Codex desktop updates. It keeps
a bounded ledger in `update-status.json` under the owned state directory. A
release can be **observed**, **staged**, or **active**:

- **Observed** means the stable GitHub release was discovered.
- **Staged** means its archive checksum, matching publisher signature, and
  isolated self-test passed and the installed startup target now selects that
  version for the next safe start.
- **Active** means a supervisor process with that version is actually running.

The updater never turns "downloaded" into "active" without proof. By default it
only stages a verified build. If the user enables **Apply Continuity updates
when idle (Codex stays open)** in the tray, Continuity requires the same staged
target and an idle backend for 30 seconds, closes and drains its relay, then
recomputes activity before launching the exact selected supervisor. A race
cancels the attempt. The existing private backend, Codex home, configured port,
and desktop process remain in place; the desktop reconnects to the new
supervisor automatically.

The successor must prove its executable identity, sampled thread IDs, the
original desktop process, and a bounded client reconnection. Failed proof rolls
back through the exact previous supervisor. Failure and rollback are suppressed
until the user explicitly retries. Unsigned and development builds can still
observe releases, but automatic staging fails closed; use the explicit manual
installation path until publisher signing is configured.

If a supervisor process dies mid-handoff, a later start resumes only a still-valid
manifest whose executable, predecessor, backend lease, supervisor status, and
desktop anchor all match. A lease already transferred to a crashed successor is
rebased only from that exact proof. Missing, expired, foreign, or post-desktop-close
handoffs become a visible failed generation while normal backend recovery remains
available; they are never silently called active.

The tray's **Check for updates now** action runs the same verified staging path
without interrupting the backend. Its activation line distinguishes staged-only,
waiting, applying, active, rolled-back, and failed states. The checked idle-apply
control is the opt-in; failed generations expose a separate retry action. When
the backend is unavailable, **Restart Continuity backend** reapplies the installed
configuration and starts only the owned supervisor; it still refuses a foreign
endpoint on the configured port. Mutating actions use the selected versioned
coordinator and are serialized so update, policy, repair, uninstall, or rollback
state cannot change concurrently.

The tray's activation menu keeps the 22:00-07:00 preset and clear action, and
also offers **Custom...**. The compact dialog defaults to the computer's local
time zone, accepts HH:mm clock inputs, explains overnight windows when the end
is earlier than the start, rejects equal times, and makes Cancel a no-op. The
selected range is passed to the same fail-closed update-policy command with its
explicit time-zone identifier.

**Recent Continuity activity...** opens a dedicated, read-only view of a small
bounded history stored under the owned Continuity state directory. It records
only allowlisted transition kinds and successful tray mutations with UTC time,
public version/state, and canned summaries. Paths, thread identifiers and
content, command output, and exception text are never written; invalid or
newer history schemas display as empty and history I/O never interrupts the
backend or active agents.

## Requirements

- Windows 11 (the initial release is tested on Windows 11 x64).
- The Microsoft Store build of the Codex desktop app.
- A user-executable Codex CLI installed by the desktop app or available on
  `PATH`.

## Platform support

| Platform | Current status |
| --- | --- |
| Windows 11 x64 | Supported by the current release and covered by the reconnect, installer, tray, and lifecycle tests. |
| macOS | Not supported yet. A credible port requires fresh validation of the desktop reconnect seam, then `launchd`, environment, packaging, and menu-bar adapters. |
| Linux | No desktop-parity claim. The supervisor core is plausibly portable for headless use, but a supported Codex desktop and reconnect seam have not been verified. |

macOS is the next meaningful validation target. Linux work should begin with a
headless supervisor only; presenting it as equivalent to the Windows desktop
utility would outrun the available evidence.

## Manual install

For a conventional installer, download `CodexContinuity-Setup.exe` from the
[latest release](https://github.com/YesterdaysLemon/codex-continuity/releases/latest)
and run it. It uses the same checksum verification and isolated self-test as
the PowerShell bootstrap. Unattended installs use:

```powershell
.\CodexContinuity-Setup.exe --silent
```

For the advanced portable path:

1. Download the versioned Windows x64 ZIP from the
   [latest release](https://github.com/YesterdaysLemon/codex-continuity/releases/latest).
2. Extract it, open PowerShell in that folder, and run:

```powershell
.\CodexContinuity.exe self-test
.\CodexContinuity.exe install --start-now
```

3. Keep using Codex normally. If it was already open, `status` reports that
   Continuity is armed. The supervised backend starts only after those existing
   desktop processes close naturally; Continuity never requests a relaunch.
4. On a later natural launch, start a small task and verify:

```powershell
.\CodexContinuity.exe status
```

The status should report `ready: true` and show that task as active. From that
point forward, newly started work belongs to the supervised backend.

Installation makes these user-level changes:

- `CODEX_APP_SERVER_WS_URL=ws://127.0.0.1:45123`
- `CODEX_SPARKLE_ENABLED=false`
- a `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CodexContinuity`
  startup entry
- by default, a separate `CodexContinuityTray` startup entry for the optional
  notification-area controller
- a repair/uninstall entry named **Codex Continuity** in Windows Installed Apps
- a stable `CodexContinuity` command on the user `PATH`
- versioned coordinator builds and owned install state under
  `%LOCALAPPDATA%\YesterdaysLemon\CodexContinuity`

Upgrading from v0.2.0 migrates ownership from the former OpenAI-adjacent data
directory without stopping its running backend. That legacy directory is
removed at the next Windows sign-in.

It never closes or restarts the running desktop app. Upgrades stage a new
version without overwriting the executable that currently owns active agents.
With the optional idle-apply policy enabled, only the supervisor process changes;
the owned private backend remains alive and the same desktop reconnects through
the stable endpoint.

## Commands

| Command | Purpose |
| --- | --- |
| `status` | Check backend health and list active thread count. |
| `handoff-plan` | Read the fail-closed lifecycle decision without stopping work or changing configuration. |
| `probe` | Inspect desktop version, update manifest, and configuration. |
| `update` | Check stable releases now and safely stage a verified newer build. |
| `update-policy --enable` | Opt into applying a verified staged Continuity build after a stable idle window. Re-run to retry a failed generation. |
| `update-policy --disable` | Keep verified Continuity updates staged until a later safe start. |
| `serve` | Run the background supervisor. |
| `install --start-now` | Configure future launches; start immediately or arm until the currently open desktop closes naturally. |
| `install --no-tray` | Install headlessly without the notification-area controller. |
| `attach` | Start or safely arm the installed supervisor without closing, signaling, or relaunching Codex. |
| `repair` | Reapply the persisted custom port and tray choice without stopping work. |
| `uninstall` | Restore owned configuration now and remove installed files at next sign-in, without killing work. |
| `rollback` | Select the previous known-good build for the next safe supervisor start. |
| `self-test` | Prove reconnect behavior in an isolated temporary Codex home. |

`probe` distinguishes a newer build advertised by OpenAI's update manifest from
an update actually offered to this PC by Microsoft Store. The manifest cannot
prove staged Store rollout or device eligibility, so `updateAvailable` remains
`null`; inspect `codexDesktopUpdate.manifestNewerThanInstalled` and follow its
`recommendedAction` instead.

`handoff-plan` reports only aggregate lifecycle blockers. The desktop is a client;
the supervised app-server owns running turns. A `handoff` result means the observed
backend is quiescent and no verified staged build is selected. It does not stop a
process or prove that the running desktop can switch backends. Any transition built
on this plan must keep the already-running Codex desktop open and fail closed if a
live switch cannot be verified.

## Local evidence

The first real migration was verified on 2026-08-20. After restarting the
desktop, the pre-existing conversation continued on the external backend; the
backend reported that same thread active, and the restarted desktop had no
bundled app-server child process. The project website and public release were
then built from inside that surviving thread.

The self-test separately creates a thread on a temporary backend, disconnects,
reconnects, and verifies the server still owns the thread. It never touches the
production desktop or user conversations.

For first attachment, inspected desktop builds do not expose a supported
in-process retarget command. Continuity therefore snapshots the identities of
the already-running Store Codex processes, binds only its gated public relay,
and keeps the private backend off until those identities disappear and it
observes a naturally empty desktop interval. If another desktop opens before
that interval, Continuity stays armed rather than guess that the new process is
safe; a later natural close lets activation continue.

## Build and prove the transport

```powershell
dotnet publish .\CodexContinuity.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o .\artifacts\win-x64
.\artifacts\win-x64\CodexContinuity.exe self-test
```

`self-test` uses a temporary `CODEX_HOME`, starts an isolated app-server on a
random loopback port, creates a thread, disconnects, reconnects, and verifies
that the same backend still owns the thread. It never connects to or restarts
the running desktop client.

## Roll back

```powershell
CodexContinuity rollback
CodexContinuity uninstall
```

`rollback` changes only the build selected for a future safe start. Uninstall
restores values captured before installation, and only while their current
values still match the ones Continuity applied. Neither command stops a running
backend or restarts the desktop. If the backend is still reachable, desktop
restarts in the current Windows session continue reconnecting to it so a second
app-server cannot contend for the same threads. The next sign-in returns Codex
to its normal bundled app-server and updater, then deletes Continuity's installed
files and logs.

If a staged build should not activate automatically, uncheck the tray's idle-apply
control or run `update-policy --disable`. Run `rollback` to select the previous
known-good build for a future safe start. The tray reports the running version,
staged target, and activation proof separately.

Safe activation can also be paused or limited to a local-time window without
weakening the idle and handoff checks:

```powershell
CodexContinuity update-policy --snooze-minutes 480
CodexContinuity update-policy --clear-snooze
CodexContinuity update-policy --activation-window 23:00-07:00
CodexContinuity update-policy --activation-window 23:00-07:00 --time-zone "Pacific Standard Time"
CodexContinuity update-policy --clear-activation-window
```

The window is an additional gate: entering it starts a fresh stable-idle proof;
time spent idle before a snooze expires or outside the configured window never
counts toward activation. Policy schema 2 also makes older supervisors fail
closed rather than silently ignoring these restrictions.

## What appears in Windows

- **Task Manager:** `Codex Continuity Supervisor` owns the resilient backend;
  `Codex Continuity Tray` is the optional, disposable status UI.
- **Notification area:** the Continuity mark appears beside or inside the
  collapsible group near Wi-Fi and volume, according to the user's Windows
  taskbar preferences.
- **Installed Apps:** `Codex Continuity` exposes repair/modify and uninstall.

Exiting or crashing the tray never stops the supervisor or its agents. The tray
does not display thread names; it reports only health, aggregate active-agent
count, update state, and activation proof. Use `--no-tray` for servers,
automation, or a completely headless setup.

## Security and operational boundary

The app-server listens on loopback only. Loopback prevents remote-network
exposure, but it is not an authentication boundary: another process on the
same Windows machine can attempt to connect to the local app-server port.
Codex Continuity does not add a shared secret because the current desktop
transport does not provide one to the external backend.

Codex Continuity does not collect telemetry, proxy prompts, store credentials,
or modify installed Store package files. App-server output stays under
`%LOCALAPPDATA%\YesterdaysLemon\CodexContinuity`; logs rotate at 5 MB with three
retained history files. Treat those local logs as potentially sensitive diagnostics.

The continuity proof covers UI disconnect/reconnect and durable thread
ownership. It does not make incompatible app-server protocol versions
compatible. If a future desktop release raises its minimum app-server version,
let active threads finish, update the Codex CLI binary used by the supervisor,
and restart only the supervisor while the desktop is closed.

See [REVERSE_ENGINEERING.md](REVERSE_ENGINEERING.md) for the version-specific
desktop observations behind the bridge.

## Release trust

Every release retains SHA-256 checksum files and publishes GitHub/Sigstore build
provenance that can be checked with `gh attestation verify`. The release
workflow defaults to unsigned artifacts and supports Microsoft Azure Artifact
Signing through GitHub OIDC plus a bounded legacy PFX mode. Signed releases use
SHA-256 Authenticode and an RFC 3161 timestamp; partial, ambiguous, mixed, or
unexpected signing configuration fails verification. Artifact Signing leaf
certificates may rotate daily, so Continuity verifies the durable subscriber
identity EKU, required Code Signing/Public Trust EKUs, and trusted chain root
rather than one leaf thumbprint or mutable subject text. A legacy PFX-installed
build remains leaf-thumbprint and root pinned, so its leaf rotation or
transition to Artifact Signing requires a manual install. Until a production
identity is configured, Windows may identify the executable as coming from an
unknown publisher and Continuity will not automatically stage its own updates.
See [the signing runbook](docs/release-signing.md) for the exact external setup
and [SECURITY.md](SECURITY.md) before bypassing any warning.

Versioned desktop delivery is continuous: after the complete CI workflow passes
for the current `main` commit, a new matching supervisor/tray version is tagged
at that exact green revision and sent through the same release workflow used by
explicit tags. Stale, fork, pull-request, failed, and already-published versions
are safe no-ops. Ordinary merges keep deploying the website without minting a
new desktop version.

## Unofficial project

Codex Continuity is not affiliated with or endorsed by OpenAI. Codex and OpenAI
are trademarks of their respective owner. This project uses undocumented and
experimental integration seams that may change without notice.

Built by [Alireza Afshan](https://alirezaafshan.com). See more
[projects](https://alirezaafshan.com/projects), or
[sponsor the work](https://github.com/sponsors/YesterdaysLemon).
