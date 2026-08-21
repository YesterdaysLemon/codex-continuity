# Codex Continuity

Codex Continuity decouples the Windows desktop UI from the process that owns
agent threads. It supervises `codex app-server` on a loopback WebSocket and
configures future desktop launches to reconnect to that server. A Microsoft
Store update can then replace or restart the UI without terminating the agent
backend.

The tool also disables the desktop's internal update component for future
launches. That removes the blue download-and-restart prompt; Store, Intune, or
another external package manager can continue updating the signed app package.
Microsoft Store's **Settings > App updates** option must remain on for the fully
automatic path. Store-delivered MSIX packages are updated by Windows in the
background rather than by this tool.

This is an experimental compatibility bridge. The WebSocket app-server
transport exists in both the public CLI and the desktop bundle, but the public
app-server README currently labels WebSocket transport experimental and
unsupported. Keep the published executable and this README together so the
configuration can be rolled back quickly.

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

## Install

Run this from the published directory:

```powershell
.\CodexContinuity.exe install
```

Installation makes three user-level changes:

- `CODEX_APP_SERVER_WS_URL=ws://127.0.0.1:45123`
- `CODEX_SPARKLE_ENABLED=false`
- a `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CodexContinuity`
  entry that launches the supervisor at logon
- a copy of the coordinator at
  `%LOCALAPPDATA%\OpenAI\CodexContinuity\CodexContinuity.exe`

It does not touch the running desktop process. The new transport is picked up
the next time the desktop starts. Use `install --start-now` only when it is
acceptable to start the second backend before that migration.

There is one unavoidable migration boundary: let work owned by the desktop's
old bundled app-server finish before the first post-install desktop restart.
After that clean launch, newly started work belongs to the supervised backend;
future UI replacement or restart no longer owns the agent process lifetime.

After the first clean desktop launch, verify:

```powershell
.\CodexContinuity.exe status
```

The result is fail-closed: if the backend health check or JSON-RPC handshake
fails, `status` returns an error rather than claiming that updates are safe.

## Roll back

```powershell
.\CodexContinuity.exe uninstall
```

Uninstall removes only values that match this tool's configuration. It does
not stop a running backend or restart the desktop. A later desktop restart
returns to its normal bundled app-server and updater.

## Operational boundary

The continuity proof covers UI disconnect/reconnect and durable thread
ownership. It does not make incompatible app-server protocol versions
compatible. If a future desktop release raises its minimum app-server version,
let active threads finish, update the Codex CLI binary used by the supervisor,
and restart only the supervisor while the desktop is closed.

See [REVERSE_ENGINEERING.md](REVERSE_ENGINEERING.md) for the version-specific
desktop observations behind the bridge.
