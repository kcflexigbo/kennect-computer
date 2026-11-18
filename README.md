# Kennect Computer

Kennect Computer gives Claude Code hands and eyes on the local desktop through
an MCP server and a per-user Windows host. The MCP server speaks JSON-RPC 2.0
over a token-authenticated named pipe to a .NET 10 host that owns window
enumeration, accessibility, focus, and screen capture.

**Current milestone: M2 core actions (Windows).** The six M1 tools plus the M2
input, perception, and batch tools work end to end; Feishu automation (M3) and
macOS (M5) arrive in later plans. See `DESIGN.md` for the full design.

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

## M2 tools

| Tool | What it does |
|---|---|
| `computer_click` | Click a snapshot node (preferred) or `x`/`y` coordinates; `button` defaults to left and `count` to 1 |
| `computer_type` | Type text; `method` is `paste` (default), `value`, or `keys`; optionally target a node |
| `computer_key` | Press a key or chord such as `Enter`, `Tab`, `Ctrl+A`, or `Alt+F4` |
| `computer_scroll` | Turn the wheel at a node or coordinate; `direction` and `amount` (1..100 notches) |
| `computer_find` | Search the selected window's accessibility tree by text and/or role without an image |
| `computer_wait` | Wait for a window, node, or text to appear or disappear, with a bounded poll and deadline |
| `computer_read_text` | Read a control's text through TextPattern or ValuePattern, capped by `maxLength` |
| `computer_batch` | Run an ordered list of up to 50 action steps serially in one call |

Every input tool requires the selected window to be foreground; the host
activates and verifies it, retries once, and otherwise returns `FOCUS_REFUSED`
with no input sent. A node ID is valid only for the snapshot generation that
produced it: acting on an older ID returns `STALE_SNAPSHOT`, and the live
element is also re-checked (role, name, identifier, and bounds) before input.

`computer_type` methods: `paste` saves the clipboard, sets the text, sends
`Ctrl+V`, and keeps the typed text on the clipboard until the target has
consumed the paste, then restores the saved text. When the target node exposes
a value or text, the host polls it for the pasted content (bounded at 2
seconds); when the target's content cannot be read (for example a paste with
no `nodeId`), it waits for the window's input queue to drain instead, also
bounded. The clipboard is restored on every path, including failure and
cancellation, and the typed text is never left behind. A target that defers
the paste beyond the bound cannot be observed: the host still restores the
clipboard after the bound, but cannot prove that target read the paste. Paste
is the default because it is fast and reliable for CJK and long text. `value`
sets the accessibility value where the control supports it. `keys` types one
Unicode event per UTF-16 code unit and never touches the clipboard.

`computer_read_text` prefers TextPattern and falls back to ValuePattern. Its
`maxLength` defaults to 10,000 characters and is capped at 50,000; the result
reports `length` and `truncated`. `computer_find` returns at most 50 matches by
default; no matches is a successful empty result, not an error.
`computer_wait` defaults to a 10,000 ms timeout (1..60,000) and a 100 ms poll;
reaching the deadline returns `ACTION_TIMEOUT`.

### Coordinate spaces

`computer_click` and `computer_scroll` accept exactly one target: a `nodeId`,
or both `x` and `y`.

- `space: "image"` (the default) means pixels of the last returned snapshot
  image for the selected window. The image carries `scale` (physical pixels per
  returned image pixel) and `sourceBounds`; the host maps the point back to
  physical pixels before injecting input. Image-space actions require a
  snapshot with `image: true`, and a window that moved or resized since that
  snapshot returns `STALE_SNAPSHOT` instead of clicking the wrong place.
- `space: "screen"` means absolute physical screen pixels. It needs no
  snapshot, but the point must lie inside the window and the window is still
  verified foreground.

### Expectations (`expect`)

`computer_click`, `computer_type`, `computer_key`, and `computer_scroll` (and
the same step kinds inside `computer_batch`) accept an optional `expect` list.
Predicates are checked against one fresh accessibility read immediately before
any focus change or input:

- `{"kind": "foreground"}` — the selected window is the foreground window.
- `{"kind": "node", "role"?, "name"?, "identifier"?, "focused"?}` — a matching
  node exists; omitted fields are wildcards.
- `{"kind": "text", "role"?, "name"?, "identifier"?, "contains"}` — a matching
  node's text/value contains the string (ordinal, case-insensitive).

The first failing predicate returns `EXPECTATION_FAILED`, names the predicate
(`foreground`, `node:<name>`, or `text:<contains>`), and sends no input.

### Batches

`computer_batch` runs ordered steps (`click`, `type`, `key`, `scroll`, `wait`,
`readText`) serially in one host call and stops at the first failure. A failed
batch reports that step's error code and message, the zero-based `failedIndex`,
and the partial per-step results; later steps do not run. A stop between steps
returns the completed steps and names the step that did not run. Optional
`snapshotBefore` and `snapshotAfter` are accessibility-only (never an image),
are best-effort, and report a problem through `warning` without stopping the
steps. The step list is capped at 50.

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

## Manual M2 acceptance

The M2 integration tests use an owned in-process window and real input, but they
cannot prove the pipe, the tool schemas, or a real app's semantics. Run this
live check yourself against a window you open, never against a real chat:

1. Run `scripts/install-dev.ps1`, restart Claude Code in this repository, then
   run `scripts/run-dev.ps1`. Confirm the tray icon appears.
2. Open a scratch app yourself (for example Notepad with a new document). Do not
   ask the tools to launch it; M2 has no launch tool.
3. Ask Claude Code to call `computer_windows`, then `computer_select` with
   `{"process": "notepad"}`. Confirm the window and its outline are returned.
4. Call `computer_snapshot` with `{"windowId": "<id>", "image": true}`. Note the
   editor's `nodeId`, the image `scale`, and `sourceBounds`.
5. Call `computer_click` with the editor `nodeId`, call `computer_type` with
   `{"text": "Kennect 汉字 123", "nodeId": "<editor>"}`, then call
   `computer_read_text` with the same node. The returned `text` must be exactly
   `Kennect 汉字 123`.
6. Call `computer_click` with `{"x": <image x>, "y": <image y>}` (omit `space`
   for image space) to place the caret by pixel, then `computer_read_text`
   again. Confirm the text is unchanged and no `STALE_SNAPSHOT` was returned.
7. Start a `computer_type` of long text or a `computer_batch` with several
   steps, and press `Ctrl+Alt+Shift+Esc` while it is running. The call must end
   with `ACTION_TIMEOUT` and "The operation was stopped."; the host stays
   running. A batch stopped between steps returns its completed steps and the
   not-run `failedIndex`; with nothing running, `computer_stop` still returns
   `{ "ok": true }`.
8. Inspect the audit log. The `computer_type` line carries only `textLength`
   (UTF-8 byte length) and `textSha256` (hex SHA-256), never the text; the
   `computer_read_text` line carries no text either.
9. Exit the host from the tray.

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

Focused host integration tests (real UIA, real input, owned window only):

```powershell
dotnet test packages/host-windows/KennectComputer.Host.Tests --filter M2Input
```

Live-desktop tests that attach to a real UIA window are opt-in and skipped by
default:

```powershell
$env:KENNECT_LIVE_DESKTOP_TESTS = "1"
dotnet test packages/host-windows/KennectComputer.Host.Tests
```

The default suites use owned in-process windows and fake hosts. The M2 input
integration focuses that owned window and sends it real input, so run it in an
interactive desktop session; it fails with a clear environment message when
foreground activation or input injection is refused. No test launches Notepad
or any other external app.

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
- `computer_type` adds `textLength` (the UTF-8 byte length) and `textSha256`
  (the lowercase hex SHA-256 digest) of the typed text. The text itself is
  never logged, and no other tool logs text: `computer_read_text` records only
  its target and outcome, and snapshot content never reaches the log.
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
