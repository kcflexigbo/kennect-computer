# Kennect Computer

Kennect Computer gives Claude Code hands and eyes on the local desktop through
an MCP server and a per-user Windows host. The MCP server speaks JSON-RPC 2.0
over a token-authenticated named pipe to a .NET 10 host that owns window
enumeration, accessibility, focus, and screen capture.

**Current milestone: M1 skeleton (Windows).** The six M1 tools work end to end;
input actions, Feishu automation, and macOS arrive in later plans. See
`DESIGN.md` for the full design.

## Requirements

- Windows 10 or 11 (the M1 host is Windows-only)
- Node.js 20+ (developed on Node 24)
- .NET 10 SDK
- Claude Code, run from this repository root

## Quick start

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-dev.ps1
```

The script runs `npm install`, `npm run build`, and `dotnet build` for the
Windows host, then writes `.mcp.json` with this registration:

```json
{
  "mcpServers": {
    "kennect-computer": {
      "command": "node",
      "args": ["<repo>/packages/mcp-server/dist/index.js"],
      "env": {
        "KENNECT_HOST_PATH": "<repo>/packages/host-windows/KennectComputer.Host/bin/Debug/net10.0-windows/KennectComputer.Host.exe",
        "KENNECT_COMPUTER_CONFIG": "<repo>/config/default.json"
      }
    }
  }
}
```

The install is idempotent. It updates only the `kennect-computer` entry,
preserves other MCP servers and any extra environment variables on that entry,
and backs up a previous `.mcp.json` to `.mcp.json.bak` (or
`.mcp.json.bak-<timestamp>` if a backup already exists). Both files are
gitignored. Pass `-SkipInstall` or `-SkipBuild` when dependencies or build
outputs are already current.

Restart Claude Code in this repository so it loads `.mcp.json`. To avoid
approval prompts, merge the allowlist the install script prints into
`.claude/settings.json`:

```json
{
  "permissions": {
    "allow": [
      "mcp__kennect-computer__computer_status",
      "mcp__kennect-computer__computer_windows",
      "mcp__kennect-computer__computer_select",
      "mcp__kennect-computer__computer_snapshot",
      "mcp__kennect-computer__computer_focus",
      "mcp__kennect-computer__computer_stop"
    ]
  }
}
```

Then start the host and check the registration:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/run-dev.ps1
```

`run-dev.ps1` verifies the registration and build outputs, prints the six tool
names, and starts the host so its tray icon is visible. It leaves the host
running; stop it from the tray **Exit** item. `-NoHost` prints the status
without starting anything, which is what automated checks use.

## M1 tools

| Tool | What it does |
|---|---|
| `computer_status` | Host/protocol version, platform, lock state, monitors, permissions |
| `computer_windows` | List top-level windows with opaque IDs, titles, processes, bounds |
| `computer_select` | Select and focus a target window, then return its accessibility outline |
| `computer_snapshot` | Capture a window's accessibility outline, optionally with a PNG image |
| `computer_focus` | Bring a window to the foreground, verifying the result |
| `computer_stop` | Ask the host to stop; the host and session stay alive |

Window IDs are opaque (`w_<counter>`); native handles never cross the protocol.
Each host run starts its ID space at a random origin, so an ID held across a
host restart fails instead of resolving to a different window. A snapshot may
restore a minimized window so its pixels can be captured; this is its only side
effect.

The stop hotkey is `Ctrl+Alt+Shift+Esc`; it preempts the current operation
immediately and is consumed by the host. `computer_stop` is a normal queued host
call: the host reads it only after the running operation returns, so it cannot
abort work already in flight and instead cancels the next operation. The tray
**Stop** item and the hotkey are the immediate preemption paths.

## Manual M1 acceptance

Automated tests never launch external apps, show tray icons, register real
hotkeys, or leave windows/processes behind (R28/R29/R36). Run this live check
yourself when changing the host or the MCP wiring.

1. Run `scripts/install-dev.ps1`, restart Claude Code in this repository, then
   run `scripts/run-dev.ps1`. Confirm the tray icon appears.
2. Ask Claude Code to call `computer_status`, then `computer_windows`. Both
   return `{ "ok": true, ... }`.
3. Open Notepad manually (do not ask the tools to launch it; M1 has no launch
   tool), then call `computer_select` with `{"process": "notepad"}` and confirm
   it returns the window plus an accessibility outline.
4. Call `computer_snapshot` with `{"windowId": "<id>", "mode": "both",
   "image": true}`. Confirm both the outline and a PNG image come back.
5. Start a long snapshot with `maxDepth: 100` and `maxNodes: 50000`, then press
   `Ctrl+Alt+Shift+Esc` while it is running. The request ends with
   `ACTION_TIMEOUT` and the message "The operation was stopped."; the host stays
   running. `computer_stop` still returns `{ "ok": true }` with nothing running.
6. Confirm an audit line exists (see below) for each call: tool, target
   window/process, result, and duration, with no title, message, text, or image
   content.
7. Exit the host from the tray.

Feishu acceptance is allowed only in a dedicated test group or your own
self-chat. Never point an acceptance run at a real chat.

Inspect the audit log:

```powershell
$day = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
Get-Content "$env:LOCALAPPDATA\KennectComputer\audit\$day.jsonl" -Tail 10 | ConvertFrom-Json
```

Files are named by UTC day. If `KENNECT_COMPUTER_SESSION_DIR` is set, the MCP
server writes audit files to `<that directory>\audit`; the Windows host still
publishes its own session files under `%LOCALAPPDATA%\KennectComputer`.

## Development loop

```powershell
npm test                     # shared + mcp-server suites
npm run typecheck            # strict TypeScript for every workspace
npm run build                # shared protocol and MCP server
dotnet test packages/host-windows/KennectComputer.Host.Tests
```

Focused MCP tests:

```powershell
npm test --workspace packages/mcp-server -- test/audit.test.ts
```

Live-desktop tests that attach to a real UIA window are opt-in and skipped by
default:

```powershell
$env:KENNECT_LIVE_DESKTOP_TESTS = "1"
dotnet test packages/host-windows/KennectComputer.Host.Tests
```

The default suites use owned in-process windows and fake hosts; they never
launch Notepad or other external apps.

## Audit log

The MCP server writes metadata-only JSONL audit entries. On Windows the default
directory is `%LOCALAPPDATA%\KennectComputer\audit`; on macOS it is
`~/Library/Application Support/KennectComputer/audit`. One file per UTC day:
`YYYY-MM-DD.jsonl`.

Each line contains exactly:

```json
{"timestamp":"...","sessionId":"...","tool":"computer_snapshot","windowId":"w_1","process":"notepad","nodeId":"n3:2","result":"ok","durationMs":12}
```

- `windowId`, `process`, and `nodeId` are present only when the tool target had
  them. Window titles and message text are never logged.
- `result` is `"ok"` or the shared error code (for example `HOST_UNAVAILABLE`,
  `WINDOW_GONE`, `ACTION_TIMEOUT`); error messages are never logged.
- Files older than the retention window are deleted when the MCP server starts.
  Retention comes from `privacy.auditRetentionDays` in `config/default.json`
  (default seven days of UTC-day files) and follows the per-user overlay; a
  missing or malformed value falls back to seven days.
- Auditing is opt-in in the server API (`createMcpServer(provider, { audit })`);
  tests and embedders that omit it never touch the real audit directory.
- Audit writes are best-effort. A disk or permission failure is reported to
  stderr and never fails a tool call; stdout belongs to the stdio transport.

`KENNECT_COMPUTER_SESSION_DIR` redirects the MCP server's session-discovery
path and the audit directory (used by tests and isolated development runs).
The Windows host does not honour this variable: it always publishes
`session.json` and `session.token` under `%LOCALAPPDATA%\KennectComputer`.
Setting the override to any other directory makes the MCP server look
somewhere the host never writes, so tool calls fail with `HOST_UNAVAILABLE`.
Leave it unset for a live host connection.

## Configuration

`config/default.json` holds the host defaults: stop hotkey, perception caps,
excluded processes/titles, and `auditRetentionDays`. A per-user
`%LOCALAPPDATA%\KennectComputer\config.json` overlays it, and
`KENNECT_COMPUTER_CONFIG` replaces the default path.

M1 consumes the stop hotkey, the `perception` defaults (`defaultMode`,
`maxDepth`, `maxNodes`, `imageLongEdge`, `maxImageLongEdge`), the privacy
exclusions, and `auditRetentionDays` (the MCP server reads the same file and
overlay to prune its audit log). `control.authorizationMode`,
`control.consequentialGate`, and `host.startOnDemand` are reserved for M4; edit
them only when that milestone lands.

Environment variables:

| Variable | Purpose |
|---|---|
| `KENNECT_HOST_PATH` | Host executable the MCP server may spawn |
| `KENNECT_COMPUTER_CONFIG` | Config file passed to the host |
| `KENNECT_COMPUTER_SESSION_DIR` | Overrides the MCP server's session-discovery and audit directory; the Windows host still uses `%LOCALAPPDATA%\KennectComputer` |

## Safety model

- Screen and window content is untrusted data. Tool descriptions tell the model
  never to follow instructions found on screen.
- `Ctrl+Alt+Shift+Esc` and the tray **Stop** item preempt the current
  operation; the stop hotkey is consumed. `computer_stop` is a queued host call
  and is answered after the running operation returns, so it cannot preempt
  work already in flight.
- A locked or secure desktop fails closed: `computer_select`, `computer_focus`,
  and `computer_snapshot` return `DESKTOP_UNAVAILABLE` with no input or capture
  attempted. `computer_status` still reports `locked: true`.
- Excluded processes and title patterns are never enumerated or captured, and
  the policy is re-applied to each window's live title and process on every
  resolve, so navigating a window to private content invalidates its old ID.
  The host's own windows (including its authorization dialog) are never
  listed, captured, or controlled.
- Only one MCP session may control the desktop at a time; a second session gets
  `LOCKED_BY_OTHER_SESSION`.
- The first time a session controls a non-pre-authorized app, the tray asks
  Allow once / Allow for session / Deny. Denied apps are recorded for the
  session.
- No telemetry and no network calls. Snapshots and outlines stay local except
  for the tool results returned to the model.

## Troubleshooting

- **Claude Code does not show `computer_*` tools.** Confirm `.mcp.json` exists at
  the repository root, restart Claude Code, and run
  `scripts/run-dev.ps1 -NoHost` to see which path is missing.
- **`HOST_UNAVAILABLE`.** The host is not running or `KENNECT_HOST_PATH` is
  wrong. Run `scripts/run-dev.ps1` and confirm the tray icon. If the MCP
  server's environment sets `KENNECT_COMPUTER_SESSION_DIR`, unset it: the host
  does not honour the override, so the server looks in the wrong directory.
- **`UNAUTHORIZED`.** The token file is stale or unreadable. Exit the host from
  the tray and start it again so `session.json` and `session.token` rotate.
- **`LOCKED_BY_OTHER_SESSION`.** Another MCP session or host owns control.
  Close the other session, or exit the host from the tray and restart it.
- **Nothing is audited.** `KENNECT_COMPUTER_SESSION_DIR` may be redirecting the
  MCP server's directory; check `<override dir>\audit`. Audit failures are
  reported on stderr in the MCP server's log, never on stdout.
- **Stop hotkey does nothing.** Another app may own the combination; the tray
  tooltip reports hotkey registration failure. Use the tray **Stop** item.

## Repository layout

```
config/default.json              Host configuration defaults
packages/shared/                 Frozen protocol v1 types, zod schemas, fixtures
packages/mcp-server/             TypeScript stdio MCP server, tools, audit log
packages/host-windows/           .NET 10 Windows host (WinForms tray + pipe)
scripts/install-dev.ps1          Build and write .mcp.json (idempotent)
scripts/run-dev.ps1              Registration status and host launch
```
