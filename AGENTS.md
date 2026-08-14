## Rules

- After finishing a feature or fixing a bug
  - Add any interface it needs for testing to the debug MCP interface.
  - Automatically build and launch the program **as Debug** (e.g. `Run.cmd` or `dotnet build` without `-c Release`). Debug MCP only listens in Debug builds.
    - Do **not** use root `Build.cmd` for this loop: it is the **Release** ship script (cleans `bin`, strips PDBs). Use it for packaging/deploy, not agent feature testing.
    - If the program from the current worktree is already running, kill only the process whose executable path matches this worktree, then run it again. Leave Debug instances from other worktrees running.
  - Use the current worktree's Debug MCP (`bin\VpsLimitMonitorMcp.exe --surface debug`, which forwards stdio to this worktree's named pipe) to test the feature or bug, if anything wrong, try to fix it and test again, until all done.
- When reading code, logs and the Debug MCP are not enough to locate a problem, use a debugger:
  - Use netcoredbg on the Debug build to set breakpoints, step, and inspect variables; feed it a command script via stdin, and drive the program to the breakpoint through the Debug MCP.
  - Use dotnet-dump to analyze hangs and crashes.
  - Only attach to the current worktree's process, run the session with a timeout, and always detach when done.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- `JeekTools.NET` is a submodule: commit and push it before the parent commit that moves its pointer.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.

## MCP

Agents talk to a running instance over a Windows named pipe, never a TCP port. `bin\VpsLimitMonitorMcp.exe` is the stdio adapter they launch; it derives the pipe name from its own folder, so a worktree's copy only ever reaches that worktree's app, and it reconnects on its own when the app restarts.

- **Two surfaces, never merged.** `--surface debug` exposes the object graph and simulation probes, and only listens in Debug builds. `--surface product` is reserved for exposing the app's own features to a user's agent; it is not served yet. The debug `invoke` tool can call anything in the process, so it must never be reachable from a user's agent.
- **Register a tool in two places**: the handler in `McpDebugServer`, and its schema in `McpDebugContract`. A tool missing from the contract is invisible to clients.
- **Anything that needs the user happens in the GUI.** Secrets the user must type are entered there, never as tool arguments; destructive actions are confirmed there.
- Tool work that touches UI state runs on the UI thread through the host's invoker.
- Each instance still writes its pipe name, pid, executable path, and workspace root to `bin\debug-mcp.json` for manual troubleshooting; the adapter does not need it to connect.

## Debug MCP Interface

Standard object-path tools from `McpHost` (roots: `Controller`, `Settings`, `App`): `describe`, `get_value`, `set_value`, `invoke`, `list_members`, `read_logs`.

App tools:

- `get_status` — full dump of accounts, services, traffic, alerts, and settings.
- `refresh` — force a full poll now and return the latest status.
- `simulate_traffic` — inject fake traffic (`usedGB`, optional `totalGB`, `account`, `serviceId`) to trigger alert logic; the injected value survives until `clear_simulation`.
- `simulate_due_date` — inject a fake expiry date (`daysFromNow` or `date`, optional `account`, `serviceId`) to trigger the renewal-reminder logic; persists until `clear_simulation`.
- `simulate_services` — inject `count` fake servers (optional `account`) to test the status panel's multi-column layout; persists until `clear_simulation`.
- `clear_simulation` — drop injected data and re-poll real data.
- `simulate_session_expired` — mark an account as logged-out to test the login-needed flow; refreshes keep treating it as expired until `clear_simulation`.
- `show_login_window` — open the WebView2 login window for an account.
- `open_stock_window` — open a provider inventory page in a new embedded browser window; optional `provider` selects NovixLink or HostYun.
- `set_settings` — change `pollIntervalMinutes` / `alertRemainingPercent` / `novixLinkStockEnabled` / `hostYunStockEnabled`.
- `get_alerts` — recent toast alert records (verifies alerts fired without watching the screen).
- `check_stock` — force stock checks now and return plan availability; optional `provider` selects NovixLink or HostYun.
- `simulate_stock` — mark a provider plan (`provider`, optional `plan` substring) as in stock to trigger the restock alert; persists until `clear_simulation`.
- `get_cookies` — list the account session's cookies for its site (name/domain/session-or-persistent/expiry), for verifying login-cookie persistence.
- `fetch_url` — fetch a same-origin URL with an account's session and return status, final URL, and body.
- `check_update` — check auto-update; `baseUrl` overrides the download base (point it at a local server to simulate a release), `apply: true` actually downloads, exits, and restarts the app.
- `get_update_status` — local version, update settings, and last check result.
- `get_storage_info` — settings storage mode and candidate directories.
- `set_storage_mode` — switch settings storage (UserDirectory / ProgramDirectory / CustomDirectory) without UI dialogs; `moveFiles` defaults to true.
