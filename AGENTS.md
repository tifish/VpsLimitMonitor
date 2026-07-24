## Rules

- After finishing a feature or fixing a bug
  - Add any interface it need for testing to debug MCP interface.
  - Automatically build and launch the program.
    - If the program from the current worktree is already running, kill only the process whose executable path matches this worktree, then run it again. Leave Debug instances from other worktrees running.
  - Use the current worktree's Debug MCP bridge to test the feature or bug, if anything wrong, try to fix it and test again, until all done.
- When reading code, logs and the Debug MCP bridge are not enough to locate a problem, use a debugger:
  - Use netcoredbg on the Debug build to set breakpoints, step, and inspect variables; feed it a command script via stdin, and drive the program to the breakpoint through the Debug MCP bridge.
  - Use dotnet-dump to analyze hangs and crashes.
  - Only attach to the current worktree's process, run the session with a timeout, and always detach when done.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.

## Debug MCP Interface

Debug builds start an MCP server (JeekTools `DebugMcpHost`) at `http://localhost:28217/mcp` (Streamable HTTP, registered in `.mcp.json` as `vpslimitmonitor-debug`). Ports are scanned upward from 28217 when taken (parallel worktrees get different ports; override with the `VPSLIMITMONITOR_MCP_PORT` env var); each instance writes a discovery file with its URL and pid to `%LOCALAPPDATA%\VpsLimitMonitor\DebugMcp\<pid>.json`.

Standard object-path tools from `DebugMcpHost` (roots: `Controller`, `Settings`, `App`): `describe`, `get_value`, `set_value`, `invoke`, `list_members`, `read_logs`.

App tools:

- `get_status` — full dump of accounts, services, traffic, alerts, and settings.
- `refresh` — force a full poll now and return the latest status.
- `simulate_traffic` — inject fake traffic (`usedGB`, optional `totalGB`, `account`, `serviceId`) to trigger alert logic; the injected value survives until `clear_simulation`.
- `simulate_due_date` — inject a fake expiry date (`daysFromNow` or `date`, optional `account`, `serviceId`) to trigger the renewal-reminder logic; persists until `clear_simulation`.
- `clear_simulation` — drop injected data and re-poll real data.
- `simulate_session_expired` — mark an account as logged-out to test the login-needed flow; refreshes keep treating it as expired until `clear_simulation`.
- `show_login_window` — open the WebView2 login window for an account.
- `set_settings` — change `pollIntervalMinutes` / `alertRemainingPercent`.
- `get_alerts` — recent toast alert records (verifies alerts fired without watching the screen).
- `check_stock` — force a stock check of the store page now and return plan availability.
- `simulate_stock` — mark a plan (`plan` substring, default first) as in stock to trigger the restock alert; persists until `clear_simulation`.
- `get_cookies` — list the account session's cookies for its site (name/domain/session-or-persistent/expiry), for verifying login-cookie persistence.
- `check_update` — check auto-update; `baseUrl` overrides the download base (point it at a local server to simulate a release), `apply: true` actually downloads, exits, and restarts the app.
- `get_update_status` — local version, update settings, and last check result.
- `get_storage_info` — settings storage mode and candidate directories.
- `set_storage_mode` — switch settings storage (UserDirectory / ProgramDirectory / CustomDirectory) without UI dialogs; `moveFiles` defaults to true.
