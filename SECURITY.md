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
- The utility does not proxy or retain prompt content.
- Authentication and thread persistence remain owned by the official Codex
  app-server and the user's existing Codex home.
- The utility does not patch, replace, or resign the installed desktop app.
- Installation changes only user environment variables, a user startup entry,
  and files under the user's local application-data directory.
