## Rules

- After finishing a feature or fixing a bug
  - Add any interface it need for testing to debug MCP interface.
  - Automatically build and launch the program.
    - If the program from the current worktree is already running, kill only the process whose executable path matches this worktree, then run it again. Leave Debug instances from other worktrees running.
  - Use the current worktree's Debug MCP bridge to test the feature or bug, if anything wrong, try to fix it and test again, until all done.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.

## Debug MCP Interface

Debug builds start an MCP server at `http://127.0.0.1:28217/mcp` (Streamable HTTP, registered in `.mcp.json` as `vpslimitmonitor-debug`). Use it to test features without touching the UI:

- `get_status` — full dump of accounts, services, traffic, alerts, and settings.
- `refresh` — force a full poll now and return the latest status.
- `simulate_traffic` — inject fake traffic (`usedGB`, optional `totalGB`, `account`, `serviceId`) to trigger alert logic; the injected value survives until `clear_simulation`.
- `clear_simulation` — drop injected data and re-poll real data.
- `simulate_session_expired` — mark an account as logged-out to test the login-needed flow.
- `show_login_window` — open the WebView2 login window for an account.
- `set_settings` — change `pollIntervalMinutes` / `alertRemainingPercent`.
- `get_alerts` — recent toast alert records (verifies alerts fired without watching the screen).
- `check_update` — check auto-update; `baseUrl` overrides the download base (point it at a local server to simulate a release), `apply: true` actually downloads, exits, and restarts the app.
- `get_update_status` — local version, update settings, and last check result.
