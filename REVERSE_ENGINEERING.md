# Reverse-engineering notes

These observations were made read-only on 2026-08-20 against the Microsoft
Store package `OpenAI.Codex_26.818.2872.0_x64__2p2nqsd0c76g0`. The Electron
ASAR's JavaScript and package metadata were extracted to a temporary directory;
no installed file, signature, or running process was modified.

## Continuity seam

- The desktop chooses its app-server WebSocket endpoint from
  `CODEX_APP_SERVER_WS_URL`, falling back to the host configuration's
  `websocket_url`. `CODEX_APP_SERVER_FORCE_CLI=1` bypasses that branch.
- The desktop's WebSocket transport advertises reconnect support. The public
  `codex app-server` command accepts `--listen ws://IP:PORT`, exposes `/readyz`
  and `/healthz`, and keeps thread state in the server rather than the client
  connection.
- The ordinary packaged Windows path starts `codex.exe app-server` over stdio
  as a child of `ChatGPT.exe`. Therefore the default backend dies with the UI.
- In the installed build, the desktop process and bundled app-server were PID
  19084 and PID 28648 during the investigation. The continuity test never
  signaled or replaced either process.

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

## Stability boundary

The public app-server README labels WebSocket transport experimental and
unsupported. Environment-variable names and minimum protocol versions can
change in a later desktop release. The coordinator therefore provides health
and JSON-RPC checks and makes no claim that an arbitrary future desktop can use
an old app-server binary. Update that backend only after its active-thread count
reaches zero.
