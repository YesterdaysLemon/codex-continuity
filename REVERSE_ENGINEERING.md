# Reverse-engineering notes

These observations were made read-only on 2026-08-20 and 2026-08-23 against the
Microsoft Store packages `OpenAI.Codex_26.818.2872.0_x64__2p2nqsd0c76g0` and
`OpenAI.Codex_26.818.4152.0_x64__2p2nqsd0c76g0`. The Electron ASAR's JavaScript
and package metadata were extracted to a temporary directory; no installed
file, signature, or running process was modified.

## Continuity seam

- The desktop chooses its app-server WebSocket endpoint from
  `CODEX_APP_SERVER_WS_URL`, falling back to the host configuration's
  `websocket_url`. `CODEX_APP_SERVER_FORCE_CLI=1` bypasses that branch.
- In build `26.818.4152.0`, the local connection is constructed once. Its
  reconnect path reuses the URL captured in that transport object. No public
  command, desktop IPC method, or deep link for replacing the local connection
  was found. Remote-host registration can add another host, but it does not
  retarget the existing local host.
- Developer-console mutation or process-memory injection would be unsupported
  in-process surgery and is outside Continuity's safety boundary.
- The desktop's WebSocket transport advertises reconnect support. The public
  `codex app-server` command accepts `--listen ws://IP:PORT`, exposes `/readyz`
  and `/healthz`, and keeps thread state in the server rather than the client
  connection.
- The ordinary packaged Windows path starts `codex.exe app-server` over stdio
  as a child of `ChatGPT.exe`. Therefore the default backend dies with the UI.
- In the installed build, the desktop process and bundled app-server were PID
  19084 and PID 28648 during the investigation. The continuity test never
  signaled or replaced either process.

## Local transport trust boundary

- The inspected desktop transport constructs a standard WebSocket from the
  configured URL. Its `CODEX_APP_SERVER_WS_URL` seam provides only an endpoint;
  no token, custom-header, client-certificate, or named-pipe option was found.
- The inspected `codex app-server --help` accepts `--listen ws://IP:PORT` (or
  stdio) and exposes no authentication-token or Windows named-pipe switch.
- A per-user named pipe would provide a stronger access-control boundary, but
  it is not compatible with the desktop's current WebSocket-only external
  transport. A private header cannot be added by Continuity alone because the
  desktop has no corresponding configuration for it.
- Continuity therefore hard-codes `127.0.0.1` in every generated endpoint and
  documents that loopback blocks remote hosts, not other processes running as
  the same Windows user. If the desktop/app-server later expose an authenticated
  local transport, that should replace the unauthenticated WebSocket seam.

## Updater seam

- `CODEX_SPARKLE_ENABLED=false` prevents inclusion of the desktop updater on
  both macOS and Windows despite the historical variable name.
- A separately exposed policy gate can also deny in-app updates after config is
  loaded. The supported managed equivalent is `features.in_app_updates = false`
  in `%ProgramData%\OpenAI\Codex\requirements.toml`.
- The Windows Store product id is `9PLM9XGG6VKS`; the production manifest is
  `https://persistent.oaistatic.com/codex-app-prod/windows-store-update.json`.
- The installed updater explicitly warns that quitting to install interrupts
  active local sessions. Its packaged Windows install path exits the Electron
  app after deployment begins, and the Windows quit path does not show the
  general active-task confirmation used on other platforms.
- At inspection time the installed package was `26.818.2872.0` and the official
  manifest advertised `26.818.3698.0`. That update was intentionally not
  installed while agents were active.

## Local proof

The self-test launched the same user-executable Codex app-server binary with an
isolated temporary `CODEX_HOME` and random loopback port. It initialized one
WebSocket connection, created a thread, disconnected, initialized a second
connection, and verified through `thread/loaded/list` that the same server still
owned the thread. The published executable passed the test repeatedly.

The installed supervisor now listens on `ws://127.0.0.1:45123`. It was started
alongside the original desktop backend, and the original desktop and app-server
PIDs remained unchanged. The Microsoft Store's visible **App updates** setting
was also inspected read-only and was On.

After the one-time desktop restart, the same pre-existing conversation appeared
as `active` through the continuity backend. The restarted desktop had no bundled
`codex.exe app-server` child; its only agent backend was the separately
supervised WebSocket server. That is the end-to-end continuity result this
project is intended to create.

## Stability boundary

The public app-server README labels WebSocket transport experimental and
unsupported. Environment-variable names and minimum protocol versions can
change in a later desktop release. The coordinator therefore provides health
and JSON-RPC checks and makes no claim that an arbitrary future desktop can use
an old app-server binary. Update that backend only after its active-thread count
reaches zero.
