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
· [Visit the product site](https://codex-continuity.alirezaafshan4.chatgpt.site)
· [Read the technical evidence](REVERSE_ENGINEERING.md)

## Install in one command

Paste this into PowerShell. The bootstrapper downloads the stable Windows x64
asset, verifies its published SHA-256 checksum, runs the isolated reconnect
self-test, and installs it without restarting Codex:

```powershell
$i="$env:TEMP\codex-continuity-install.ps1"; curl.exe -fsSL https://github.com/YesterdaysLemon/codex-continuity/releases/latest/download/install.ps1 -o $i; powershell.exe -NoProfile -ExecutionPolicy Bypass -File $i -StartNow
```

Agents and automation can inspect the exact plan without downloading or
changing anything:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $i -Plan -Json
```

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
- Binds only to `127.0.0.1`; it does not expose Codex over the network.
- Removes the desktop's blue in-app update prompt on future launches.
- Leaves signed package delivery to Microsoft Store, Intune, or another
  external package manager.
- Reports backend health and active thread count through `status`.
- Includes an isolated reconnect self-test and a reversible install.

Microsoft Store's **Settings > App updates** option must remain on for the fully
automatic path. Store-delivered MSIX packages are updated by Windows in the
background rather than by this tool.

## Requirements

- Windows 11 (the initial release is tested on Windows 11 x64).
- The Microsoft Store build of the Codex desktop app.
- A user-executable Codex CLI installed by the desktop app or available on
  `PATH`.

## Manual install

1. Download the versioned Windows x64 ZIP from the
   [latest release](https://github.com/YesterdaysLemon/codex-continuity/releases/latest).
2. Extract it, open PowerShell in that folder, and run:

```powershell
.\CodexContinuity.exe self-test
.\CodexContinuity.exe install --start-now
```

3. Let work owned by the old bundled backend finish, then restart the Codex
   desktop once. That is the one-time migration boundary.
4. Start a small task and verify:

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
- a repair/uninstall entry named **Codex Continuity** in Windows Installed Apps
- versioned coordinator builds and owned install state under
  `%LOCALAPPDATA%\OpenAI\CodexContinuity`

It never closes or restarts the running desktop app. Upgrades stage a new
version and redirect only the next safe supervisor start; they do not overwrite
the executable that currently owns active agents.

## Commands

| Command | Purpose |
| --- | --- |
| `status` | Check backend health and list active thread count. |
| `probe` | Inspect desktop version, update manifest, and configuration. |
| `serve` | Run the background supervisor. |
| `install --start-now` | Configure future launches and start the supervisor. |
| `uninstall` | Remove future-launch and startup configuration without killing work. |
| `rollback` | Select the previous known-good build for the next safe supervisor start. |
| `self-test` | Prove reconnect behavior in an isolated temporary Codex home. |

## Local evidence

The first real migration was verified on 2026-08-20. After restarting the
desktop, the pre-existing conversation continued on the external backend; the
backend reported that same thread active, and the restarted desktop had no
bundled app-server child process. The project website and public release were
then built from inside that surviving thread.

The self-test separately creates a thread on a temporary backend, disconnects,
reconnects, and verifies the server still owns the thread. It never touches the
production desktop or user conversations.

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
.\CodexContinuity.exe rollback
.\CodexContinuity.exe uninstall
```

`rollback` changes only the build selected for a future safe start. Uninstall
restores values captured before installation, and only while their current
values still match the ones Continuity applied. Neither command stops a running
backend or restarts the desktop. A later desktop restart after uninstall
returns to its normal bundled app-server and updater.

## Security and operational boundary

The app-server listens on loopback only. Loopback prevents remote-network
exposure, but it is not an authentication boundary: another process on the
same Windows machine can attempt to connect to the local app-server port.
Codex Continuity does not add a shared secret because the current desktop
transport does not provide one to the external backend.

Codex Continuity does not collect telemetry, proxy prompts, store credentials,
or modify installed Store package files. App-server output stays under
`%LOCALAPPDATA%\OpenAI\CodexContinuity`; logs rotate at 5 MB with three retained
history files. Treat those local logs as potentially sensitive diagnostics.

The continuity proof covers UI disconnect/reconnect and durable thread
ownership. It does not make incompatible app-server protocol versions
compatible. If a future desktop release raises its minimum app-server version,
let active threads finish, update the Codex CLI binary used by the supervisor,
and restart only the supervisor while the desktop is closed.

See [REVERSE_ENGINEERING.md](REVERSE_ENGINEERING.md) for the version-specific
desktop observations behind the bridge.

## Unofficial project

Codex Continuity is not affiliated with or endorsed by OpenAI. Codex and OpenAI
are trademarks of their respective owner. This project uses undocumented and
experimental integration seams that may change without notice.

Built by [Alireza Afshan](https://alirezaafshan.com). See more
[projects](https://alirezaafshan.com/projects), or
[sponsor the work](https://github.com/sponsors/YesterdaysLemon).
