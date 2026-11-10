# Kennect Computer — Design & Features

**Status:** Draft for review — rev 2
**Date:** 2026-11-10
**Owner:** Kenneth Igbo
**Scope:** Personal development tool — not a Butterbase product
**Platforms:** Windows and macOS (v1)
**Working name:** Kennect Computer (sibling to the Kennect browser MCP server)

**Revision note (rev 2):** macOS is now a first-class v1 platform, not a future item. The flagship flow is replying to Feishu messages, not Qoder; Qoder becomes a secondary smoke test. Actions the user explicitly requests execute without an interactive approval gate. The tool surface gains batched actions and paste-by-default text. Milestones are restructured around capability and platform parity.

---

## 1. Intent and why

Today the agent can see and act on Chrome through the Kennect browser MCP server. Every task that leaves the browser — Feishu, Qoder, Cursor, native Windows or macOS apps, installers, GUIs with no API — falls back to the human.

Kennect Computer gives the agent the same kind of control over the **whole desktop**, on **Windows and macOS**, with the agent running on that machine: list windows, see the screen, understand what is on it, and click/type into any app. It follows the same architecture as the browser tool: a local MCP server the agent calls, backed by a local native host that does the OS work.

The first proof is concrete and daily: **reply to Feishu (Lark) messages end to end** — open the app, find the conversation, read it, compose from the user's instruction, paste, send, and report what went out. Qoder's Report Issue flow remains as a secondary smoke test.

### Why now

- Messaging is where the browser boundary bites most often: Feishu desktop has no practical API for the agent, and replying is a daily task.
- Claude Code's own computer-use MCP server is macOS-only and vision-only; on Windows there is no equivalent. Neither is a shared cross-platform tool.
- The Kennect pattern is proven: local MCP server + local native host + per-target scoping + semantic actions with screenshot fallback.
- Both machines are in daily use, and the agent runs locally on each.

## 2. Goals, non-goals, anti-goals

### Goals

1. **One tool surface, two platforms.** The same MCP tools work whether the agent runs on Windows or macOS, with native fidelity on each.
2. **See:** capture a single window (or monitor) and return it to the model, with a compact accessibility outline alongside the image.
3. **Understand:** enough structure — window identity, focused control, modal state, interactive elements — that the model can orient without guessing from pixels alone.
4. **Act:** semantic actions first, coordinate actions as fallback; the full surface — click, type, key, scroll, drag, hover, focus, window, clipboard, launch, stop — plus ordered batches.
5. **Autonomy per instruction:** actions the user asked for, including sending a message, execute without an interactive gate. The user's instruction is the authorization; the stop hotkey and audit log are the mechanical brakes.
6. **Recover:** fresh observation before/after actions, stale-target detection, wrong-window guards, clear errors.
7. **Stay interruptible:** a global stop hotkey and a visible "agent has control" indicator on both platforms.
8. **Match Kennect's ergonomics:** same tool style, per-target selection, explicit per-app authorization.
9. **Daily-driver install:** per-user tray/menu-bar app, on-demand start, MCP registration, no developer tooling needed after setup.

### Non-goals (v1)

- Not a multi-tenant or cloud service.
- Not Linux.
- Not background/isolated-desktop operation — foreground control is accepted in v1; background moves to v2.
- Not an unattended monitor or workflow engine; flows are request-driven.
- Not a replacement for Kennect's browser tools; Chrome work stays on the browser MCP.
- No password-manager, banking, Windows Security, or UAC automation.
- No credential storage.

### Anti-goals (would make this a failure)

- A coordinate-only clicker that breaks when the app moves or scales.
- An agent that can hijack the mouse with no reliable way to stop it.
- Acting on the wrong window because the target changed between snapshot and click.
- Sending messages or taking consequential actions the user did not ask for.
- Requiring an interactive approval for every action the user explicitly requested.
- A tool that requires the user to keep the app in a fixed position or resolution.

## 3. Product shape

```
Claude Code (or another MCP client)
        │  MCP over stdio
        ▼
┌──────────────────────────────┐
│ kennect-computer-mcp         │  TypeScript MCP server (one per platform)
│  • tool definitions          │  • window/session scoping
│  • output shaping            │  • stale-target checks
│  • policy/authorization      │  • audit log
│  • action queue              │  • platform transport adapter
└──────────────┬───────────────┘
               │  JSON-RPC 2.0 over a local socket
               │  Windows: named pipe  •  macOS: Unix domain socket
               ▼
┌──────────────────────────────┐      ┌──────────────────────────────┐
│ Windows host (C#/.NET 10)    │      │ macOS host (Swift)           │
│  • window enumeration        │      │  • window enumeration        │
│  • UI Automation (UIA3)      │      │  • Accessibility (AX)        │
│  • SendInput                 │      │  • CGEvent                   │
│  • WGC / PrintWindow         │      │  • ScreenCaptureKit          │
│  • Windows.Media.Ocr         │      │  • Vision OCR                │
│  • tray + stop hotkey        │      │  • status item + stop hotkey │
└──────────────┬───────────────┘      └──────────────┬───────────────┘
               │  Win32 / UIA / WinRT                 │  AX / CG / AppKit
               ▼                                      ▼
          Windows desktop                        macOS desktop
```

### Why split server and host

- The MCP server stays TypeScript, matching Kennect and Claude Code's ecosystem, and is shared across platforms.
- Windows fidelity lives in C#/.NET: UIA, `SendInput`, DPI APIs, WinRT capture. Node bindings for these are weak or unmaintained.
- macOS fidelity lives in Swift: `AXUIElement`, `CGEvent`, ScreenCaptureKit, Vision. Bridging these from Node is worse than a small native host.
- The split also gives a clean kill point: stopping the host releases all control immediately.

### Why two hosts, one protocol

The platform-specific work is unavoidable — UIA and AX are different worlds. Keeping the protocol platform-neutral (opaque window IDs, a generic node vocabulary, generic actions) means one MCP server, one tool surface, one acceptance suite, and two thin hosts. Platform quirks stay inside the hosts.

### Why a tray / menu-bar app

- Shows when the agent has control.
- Hosts the stop hotkey.
- Hosts per-app authorization prompts.
- Requests OS permissions (Windows: none beyond user session; macOS: Accessibility and Screen Recording) with a stable app identity.
- Survives MCP server restarts; one instance per logged-in user.

## 4. Capabilities and tool surface

Tool names use the `computer_` prefix. Window IDs are opaque strings on both platforms; node IDs are ephemeral and valid only for the snapshot that produced them.

### Session and targeting

| Tool | Purpose |
|---|---|
| `computer_status` | Host version, control lock, platform, monitors, scale, authorization state, locked/secure state, permission status. |
| `computer_windows` | List top-level windows: id, title, process, PID, bounds, focused, minimized, visible. |
| `computer_select` | Select a window by id/title/process; returns a compact snapshot. Selection is explicit so later actions cannot drift to another window. |

### Perception

| Tool | Purpose |
|---|---|
| `computer_snapshot` | The primary observation. Returns a downscaled image plus an accessibility outline (role, name, identifier, bounds, enabled, focused, actions) with node IDs valid for this snapshot. Params: window/monitor, `vision`/`a11y`/`both`, max depth, max nodes, image on/off. |
| `computer_find` | Search the selected window's accessibility tree by text/role; returns matching nodes without an image. |
| `computer_ocr` | OCR of a region for canvas/non-accessible content. |
| `computer_read_text` | Read a control's text via text/value patterns. |
| `computer_wait` | Wait for a window, element, or text to appear/disappear, with timeout. |

### Actions

| Tool | Purpose |
|---|---|
| `computer_click` | Click a node (preferred) or coordinates; button, count, modifiers. |
| `computer_type` | Type text; `method: "paste"` (default), `"value"`, or `"keys"`; optionally target a node. |
| `computer_key` | Key or chord (Enter, Ctrl/Cmd+A, Alt+F4, Cmd+Q…). |
| `computer_scroll` | Scroll at a node/coordinate, direction and amount. |
| `computer_drag` | Drag from a node/coordinate to another. |
| `computer_hover` / `computer_move` | Pointer positioning. |
| `computer_focus` | Bring a window to the foreground or focus a control. |
| `computer_window` | Move, resize, minimize, restore, close (close is gated when configured). |
| `computer_clipboard` | Read/write clipboard; explicit and audited. |
| `computer_launch` | Launch an app by path or known alias. |
| `computer_batch` | Execute an ordered list of actions serially in one call: stop at the first error, per-step results, optional snapshot before/after. |
| `computer_stop` | Abort the current action and release control. |

Mutating action tools accept an optional `expect` list — predicates the host evaluates against a fresh accessibility read immediately before input. A failed predicate returns `EXPECTATION_FAILED` and no input is sent. This is how the Feishu flow asserts the right conversation before Send.

### Concurrency and batching

There is one mouse and one keyboard, so input actions are never executed in parallel. The host serializes all mutating actions. `computer_batch` exists so the model can express a sequence in one call without a round trip between steps; inside a batch, steps run in order and a failed step stops the batch.

Read-only calls — status, window listing, find, read_text, OCR — may run concurrently with each other. Concurrent input requests from the client are queued in arrival order; the server never interleaves their effects.

### Output shape

Every action result includes:

- `ok` / error with a machine-readable code and a human message.
- The window identity acted on (opaque id + process + class/bundle; title is metadata, not identity).
- What changed: focused window/element after the action, when cheap.
- `needs_observation: true` when the caller should re-snapshot.
- For batches: per-step outcome and the index of the failed step.

Images are returned as MCP image content. Default long edge 1568 px (cost/latency); `computer_snapshot` accepts up to 2576 px for detail work. Each image carries `scale` and `sourceBounds` so coordinates can be mapped back.

## 5. Perception model

The model should not have to guess from pixels when structure exists.

### Three layers

1. **Accessibility tree (primary)** — UI Automation on Windows, AX on macOS. Gives roles, names, identifiers, bounds, enabled/focused state, and supported actions. This is the desktop equivalent of Kennect's accessibility snapshot.
2. **Screenshot (layout and visual state)** — confirms what the user sees, exposes canvas content, styling, error banners, and positions that the accessibility tree reports poorly.
3. **OCR (fallback)** — for canvas apps and controls whose text is not exposed. Windows.Media.Ocr and macOS Vision both run locally; Tesseract remains an optional fallback.

### The snapshot contract

`computer_snapshot` returns a compact state header plus the tree:

```
app: Feishu (Feishu.exe, pid 73760)
window: "Feishu" (id w_5f3a…, 1600x1000 at 0,0, scale 1.5)
focused: [n42] button "Send"
modal: none
nodes: 214 shown / 1432 total (depth 12, interactive + text)
```

Hidden/offscreen/disabled nodes are filtered by default. Interactive nodes and text are kept; purely decorative containers are collapsed. Each node carries a `nodeId` valid only for this snapshot.

### Why hybrid

- Accessibility-only is fast and precise but patchy — Electron/Chromium apps sometimes expose little until an accessibility client asks; custom-drawn UI exposes nothing.
- Vision-only is universal but slow, expensive, and prone to coordinate mistakes.
- The combination lets the model target semantically and verify visually, falling back to coordinates only when the tree is insufficient.

### Electron/Chromium notes

- Feishu is Electron on both platforms. On Windows, Chromium enables accessibility on demand when a UIA client connects; on macOS, Electron exposes an AX tree.
- If the tree is empty, the host retries after focusing the window, and the snapshot falls back to vision-only with a warning.
- Webviews can appear as separate accessibility subtrees; the host flattens them into the window outline and dedupes by bounds + identifier.

## 6. Action model

### Semantic first

- `computer_click` with a `nodeId` uses the platform's default action or a supported activation action (Invoke/Toggle/SelectionItem/ExpandCollapse on Windows; `AXPress`, `AXConfirm`, `AXOpen` on macOS).
- `computer_type` with a `nodeId` uses a value-setting action where supported, otherwise focuses the control and inputs text.
- If no semantic action is supported, fall back to bounds-center clicking, then to coordinates.

### Text input

- Paste is the default (`method: "paste"`): save the current clipboard, set the text, send Ctrl+V / Cmd+V, restore the clipboard. This is faster and far more reliable for CJK and long text in Electron contenteditables.
- `"value"` sets the accessibility value directly where the control supports it.
- `"keys"` types character by character using Unicode input (SendInput with `KEYEVENTF_UNICODE`; CGEvent with `CGEventKeyboardSetUnicodeString`). Slower; fallback only.
- Clipboard writes are audited and gated when the policy gate is enabled.

### Coordinate mapping

- Hosts are per-monitor scale aware. Screenshots are captured at physical pixels, downscaled for the model, and returned with `scale` and `sourceBounds`.
- Coordinates accepted by tools are in the **returned image's pixel space** unless `space: "screen"` is set. The host maps back to physical pixels before injecting input.
- This mirrors the Kennect lesson: never mix screenshot pixels, CSS pixels, and device pixels silently. macOS Retina backing scale and Windows per-monitor DPI are the same problem with different names.
- Coordinate actions require a snapshot of the selected window. If the window's bounds changed since that snapshot, the action is refused with `STALE_SNAPSHOT` unless the caller sets `space: "screen"`; in screen space the host maps absolute coordinates and still verifies the window is foreground.

### Stale targets

- Node IDs embed the snapshot generation. Acting on an ID from an older snapshot returns `STALE_SNAPSHOT` with instructions to re-observe.
- A node's identity is re-checked (identifier + name + role + bounds hash) before acting.
- Window identity is the native window handle plus process and window class/bundle identifier. The title is metadata and may change at any time (unread counts, document names) without invalidating the selection; a title change alone never refuses an action.
- If the handle, process, or class/bundle changed materially, the action is refused and the selection is cleared.

### Pre-action expectations

- Mutating actions accept an `expect` list: predicates evaluated in the host against a fresh accessibility read immediately before input — the selected window is foreground; an element with a given role/name/identifier exists or is focused; a text value contains a string.
- A failed predicate returns `EXPECTATION_FAILED`, performs no input, and names the predicate that failed. Model verification is good; mechanical verification is better.

### Focus and foreground rules

- Before input, the host verifies the target window is foreground; if not, it activates the window and re-verifies.
- Windows may refuse foreground changes (foreground lock); macOS may keep another app frontmost or the target may be on another Space. The host retries once, then returns `FOCUS_REFUSED` rather than typing into the wrong app.

### Authorization and consequential actions

- **The user's instruction is the authorization.** When the user asks for a reply, the read/compose/paste/send sequence is the requested action and runs without an interactive prompt.
- **Per-app authorization** still applies the first time a session controls a process: the tray/menu bar asks Allow once / Allow for session / Deny. This authorizes controlling the app, not each action.
- An optional **consequential gate** (`approvalMode: "countdown"`) can be enabled per app for unsupervised use. Default is off. When on, flagged actions — Send, Submit, Delete, Pay, Buy, Uninstall, Format, window close, launch, clipboard writes — show a cancellable countdown.
- The model is instructed not to take consequential actions beyond the user's instruction.
- The stop hotkey aborts any action at any time.

## 7. Session and window model

- **Control lock:** only one MCP session may control the desktop at a time. A named mutex on Windows and an advisory file lock on macOS, plus a lock file. A stale lock is cleared when the owning process is gone.
- **Window selection:** `computer_select` fixes the target. Every subsequent action carries the selected window's identity; actions on a different window require an explicit re-select.
- **Per-app authorization:** the first time a session controls a process, the tray/menu bar asks Allow once / Allow for session / Deny. Apps named `preAuthorized` in config (Feishu) skip this prompt; OS permission dialogs are never bypassed. Denied apps are recorded for the session.
- **App identity:** process path + PID + window class/bundle identifier. Title is metadata; an app restart invalidates stale authorization.
- **Multi-monitor:** snapshot accepts `monitor: all|primary|<id>`; default is the selected window's monitor.

## 8. Safety and permissions

### Hard rules

1. **Stop always works.** Global hotkey — default `Ctrl+Alt+Shift+Esc` on Windows, `⌃⌥⇧⎋` on macOS — and a tray/menu Stop abort the current action and release input. The key is consumed.
2. **Locked or secure desktops fail closed.** Windows lock/UAC and macOS lock screen, fast user switching, or secure event input return `DESKTOP_UNAVAILABLE`; no input is attempted.
3. **Sensitive windows are excluded by default.** Password managers, banking apps, Windows Security, macOS Keychain Access, System Settings privacy panes, and the host's own authorization dialogs are never captured or controlled. The exclusion list is configurable.
4. **No credentials.** The tool does not read password fields, keychains, or browser credential stores. Typing a password is allowed only as explicit user-supplied text.
5. **Audit everything.** JSONL log of tool, target window, node/coordinates, text length/hash (never content), result, duration, and authorization decision.
6. **macOS permissions are explicit.** The host requests Accessibility and Screen Recording on first run, explains why, and reports `PERMISSION_MISSING` with setup guidance when a grant is absent. The app uses a stable bundle identity so grants survive rebuilds.

### Prompt-injection posture

Screen content is untrusted data. Tool descriptions say so. The host never executes instructions found on screen; the model is the only actor, and its actions pass through the policy layer. This is a mitigation, not a guarantee — the stop hotkey and per-app authorization are the real boundaries.

### Data handling

- Screenshots and trees stay local except for the images the MCP server returns to the model.
- No telemetry.
- Audit log retention is configurable; default 7 days, rotated.
- Message content is not written to the audit log.

## 9. Reliability and error handling

| Failure | Behavior |
|---|---|
| Stale node ID | `STALE_SNAPSHOT`; caller re-snapshots. |
| Window gone | `WINDOW_GONE`; selection cleared. |
| Focus refused | One retry, then `FOCUS_REFUSED`. |
| Accessibility tree empty | Retry after focus; vision-only snapshot with a warning. |
| App busy/hung | `APP_NOT_RESPONDING` after a 2 s probe. |
| Secure desktop / lock / secure input | `DESKTOP_UNAVAILABLE`, no input attempted. |
| Host disconnected | MCP server returns `HOST_UNAVAILABLE`; it respawns the host once. |
| Action timeout | Abort, re-snapshot, report partial state. |
| Batch step fails | Stop the batch; return per-step results and the failed index. |
| macOS permission missing | `PERMISSION_MISSING` with the exact grant to enable. |
| Pre-action expectation fails | `EXPECTATION_FAILED`; no input sent; the failed predicate is named. |
| Coordinates stale after move/resize | `STALE_SNAPSHOT` unless `space: "screen"` is set. |

Every mutating action is followed by a cheap verification (focused element, window title, or a caller-requested snapshot). The agent is responsible for the semantic check; the tool guarantees the mechanical one.

## 10. Architecture and repo layout

```
kennect-computer/
  DESIGN.md
  README.md
  package.json                    # workspace root
  packages/
    mcp-server/                   # TypeScript, stdio MCP
      src/
        index.ts
        tools/
        protocol/
        policy/
        audit/
        transport/                # named pipe / Unix socket selection
    shared/                       # JSON schema + TS types for the protocol
    host-windows/                 # .NET 10 WinExe
      KennectComputer.Host/
        Program.cs
        Windows/                  # enumeration, focus, DPI
        Capture/                  # WGC / PrintWindow / BitBlt
        Uia/                      # tree walking, patterns
        Input/                    # SendInput
        Ocr/                      # Windows.Media.Ocr
        Ipc/                      # named pipe server
        Tray/                     # status, authorization, stop
    host-macos/                   # Swift menu-bar app
      KennectComputerHost/
        AppDelegate.swift
        Windows/                  # CGWindowList + AX window info
        Ax/                       # AX tree walking, actions
        Capture/                  # ScreenCaptureKit
        Input/                    # CGEvent, pasteboard
        Ocr/                      # Vision
        Ipc/                      # Unix domain socket server
        MenuBar/                  # status item, authorization, stop
  config/
    default.json
  scripts/
    install-dev.ps1
    run-dev.ps1
    install-dev.sh
    run-dev.sh
    acceptance-feishu.md
    acceptance-suite.md
```

### Runtime

- MCP server: Node 20+ (Node 24 is installed), launched by Claude Code over stdio. Install scripts register the server and emit the recommended Claude Code tool allowlist.
- Windows host: .NET 10 (`net10.0-windows`), self-contained single-file publish, current-user process (not a service) so it can see the interactive desktop and UIA.
- macOS host: Swift 5.10+, macOS 14+ deployment target, menu-bar app, signed with a stable identity for TCC grants. The development machine is a MacBook on macOS 26.6.1 (Apple silicon); builds are arm64 (universal optional). M0b and M5 are built and tested there via Claude Code with Xcode installed.
- IPC: named pipe `\\.\pipe\kennect-computer-<user-sid>` on Windows; Unix domain socket in `~/Library/Application Support/KennectComputer/` (mode 0600) on macOS. JSON-RPC 2.0, length-prefixed frames.
- Auth: 32-byte token in `%LOCALAPPDATA%\KennectComputer\session.token` or `~/Library/Application Support/KennectComputer/session.token`, ACL to the current user, rotated per host start. The MCP server reads it; the host rejects unknown tokens.
- Single instance: named mutex on Windows; lock file plus `flock` on macOS. A second instance brings up the existing tray/menu bar and exits.

### Why not native messaging

Chrome native messaging is tied to the browser. The desktop host should work with or without Chrome on either platform, and a local socket is easier to debug, version, and keep alive across MCP restarts.

## 11. IPC protocol (v1)

JSON-RPC methods:

| Method | Direction | Notes |
|---|---|---|
| `hello` | server → host | token, protocol version, client name. |
| `status` | server → host | lock state, platform, monitors, scale, permissions. |
| `windows.list` | server → host | top-level windows. |
| `window.select` | server → host | returns identity + snapshot. |
| `snapshot` | server → host | mode, depth, node cap, image options. |
| `find` | server → host | text/role query. |
| `action` | server → host | discriminated union: click/type/key/scroll/drag/focus/window/clipboard/launch. |
| `batch` | server → host | ordered action list; serial execution; per-step results. |
| `wait` | server → host | condition + timeout. |
| `stop` | server → host | abort. |
| `event` | host → server | `authorization_required`, `permission_missing`, `window_changed`, `host_stopping`. |

Events are fire-and-forget; the server turns `authorization_required` into a tool error or a pending state, never a silent block.

Protocol vocabulary is platform-neutral: `windowId` is an opaque string, node fields are role/name/identifier/bounds/enabled/focused/actions, and action names are generic. Platform-specific values may appear in `role` and `actions`, but callers never need to branch on OS.

## 12. Platform implementation notes

### Windows

| Concern | Choice |
|---|---|
| Window enumeration | `EnumWindows` + `GetWindowThreadProcessId`; filter cloaked/tool windows via DWM (`DWMWA_CLOAKED`). |
| Accessibility | FlaUI.UIA3 (managed UIA3) with cache requests; tree walker limited by depth/node caps. |
| Capture | Window: `PrintWindow(PW_RENDERFULLCONTENT)` first, `Windows.Graphics.Capture` when available; monitor/desktop: `BitBlt` or WGC. Fallback `CopyFromScreen`. |
| Input | `SendInput` for mouse/keyboard; never `keybd_event`/`mouse_event`. Unicode typing via `KEYEVENTF_UNICODE`. |
| DPI | `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`; convert client/screen/image coordinates explicitly. |
| Focus | `SetForegroundWindow` with the `AttachThreadInput` workaround; verify foreground after. |
| Lock/UAC | Fail closed when the input desktop is not the default desktop or the session is locked. |
| OCR | `Windows.Media.Ocr` (local, language packs); optional Tesseract. |
| Tray | WinForms `NotifyIcon`; global hotkey via `RegisterHotKey`. |
| Packaging | `dotnet publish -r win-x64 --self-contained`; no installer required for v1. |

### macOS

| Concern | Choice |
|---|---|
| Window enumeration | `CGWindowListCopyWindowInfo` for the window list; `AXUIElement` for app windows; filter zero-alpha and off-screen windows. |
| Accessibility | AX API (`AXUIElementCopyAttributeValue`, `AXUIElementPerformAction`) with batched attribute reads and node caps. |
| Capture | ScreenCaptureKit (`SCScreenshotManager` / `SCStream`); `CGWindowListCreateImage` as fallback. Capture at backing scale. |
| Input | `CGEvent` for mouse and keyboard; Unicode text via `CGEventKeyboardSetUnicodeString`; paste via `NSPasteboard`. |
| Scale | `NSScreen.backingScaleFactor`; convert between AX screen coordinates and image pixels explicitly. |
| Focus | `NSRunningApplication.activate`, `AXRaise`; verify the frontmost app afterward. |
| Lock/secure input | `CGSessionCopyCurrentDictionary` lock state; `IsSecureEventInputEnabled()`; fail closed. |
| Permissions | Accessibility via `AXIsProcessTrustedWithOptions`; Screen Recording via `CGPreflightScreenCaptureAccess` / `CGRequestScreenCaptureAccess`. |
| OCR | Vision `VNRecognizeTextRequest` (English and CJK). |
| Menu bar | `NSStatusItem`; global hotkey via Carbon `RegisterEventHotKey`. |
| Packaging | arm64 build (universal optional), stable bundle identifier, self-signed certificate in the login keychain for persistent TCC grants. |

### Known limitations

- Some apps render black in `PrintWindow` when minimized; restore before capture.
- Accessibility can be slow on huge trees; cache requests and node caps are mandatory.
- Foreground activation can be refused (Windows foreground lock; macOS Spaces/app activation policies); fail rather than type blindly.
- UAC, the Windows lock screen, the macOS lock screen, and secure event input cannot be automated by design.
- Electron webviews may expose duplicate subtrees; dedupe by bounds + identifier.
- macOS TCC grants can reset if the bundle identity changes; rebuilds must keep a stable signing identity.
- Feishu's accessibility tree quality is measured in the spike; the vision fallback exists for a reason.

## 13. Configuration

`config/default.json` (per-user override supported on both platforms):

```json
{
  "host": {
    "startOnDemand": true
  },
  "control": {
    "stopHotkey": { "windows": "Ctrl+Alt+Shift+Esc", "macos": "Ctrl+Alt+Shift+Esc" },
    "authorizationMode": "per-app",
    "consequentialGate": "off",
    "consequentialCountdownMs": 3000
  },
  "perception": {
    "defaultMode": "both",
    "maxDepth": 12,
    "maxNodes": 1500,
    "imageLongEdge": 1568,
    "maxImageLongEdge": 2576
  },
  "privacy": {
    "excludedProcesses": [
      "KeePass.exe", "1Password.exe", "Bitwarden.exe",
      "WindowsSecurity.exe", "LogonUI.exe",
      "Keychain Access", "SecurityAgent"
    ],
    "excludedTitles": ["*password*", "*bank*"],
    "auditRetentionDays": 7
  },
  "apps": {
    "Feishu": { "consequentialGate": "off", "preAuthorized": true }
  }
}
```

`preAuthorized: true` skips the first-use app prompt for that app; it never bypasses OS permissions (macOS Accessibility/Screen Recording, Windows secure desktop). Config is per-user; the host watches the file and reloads non-structural changes.

## 14. Logging and privacy

- Audit log: `%LOCALAPPDATA%\KennectComputer\audit\YYYY-MM-DD.jsonl` on Windows; `~/Library/Application Support/KennectComputer/audit/YYYY-MM-DD.jsonl` on macOS.
- Fields: timestamp, session id, tool, app/window identity, node id or coordinates, text length/hash for typed text, result, duration, authorization decision.
- Screenshots are not persisted by default; `computer_snapshot` can opt into saving to the user data directory for debugging, with rotation.
- No network telemetry. The only outbound data is what the model receives as tool results.

## 15. Performance targets

| Operation | Target |
|---|---|
| `windows.list` | < 100 ms |
| Accessibility-only snapshot (typical app) | < 300 ms |
| Snapshot with image | < 700 ms |
| Click / type / key round trip | < 150 ms |
| `computer_batch` overhead per step | < 20 ms beyond the action itself |
| OCR of a 1200×800 region | < 500 ms |
| Host cold start | < 2 s |

Image budget: default 1568 px long edge; the model can request zoomed crops instead of full-resolution screenshots. Targets apply on both platforms; macOS window capture may be slower on the first call while ScreenCaptureKit warms up.

## 16. First milestone: Feishu reply flow

### Flow

1. `computer_status` → confirm the host is healthy, desktop unlocked, Feishu not excluded.
2. `computer_windows` → find the Feishu window (process `Feishu` on Windows; process/bundle identifier measured at spike time on macOS).
3. `computer_select` → focus and snapshot.
4. Locate the target conversation — search box or chat list — and open it.
5. Read the recent messages; identify the one to reply to.
6. Compose the reply from the user's instruction.
7. `computer_click` the message input, `computer_type` with `method: "paste"`, and `computer_read_text` to confirm the draft matches the composed text.
8. Assert the open conversation header matches the user-named conversation, click Send (or press Enter, per Feishu's behavior), confirm the reply appears in the thread, and report the conversation, the sent text, and a confirmation screenshot.

### Acceptance criteria

- The agent completes the flow without the user touching the mouse or keyboard.
- The sent message matches the composed text and appears in the thread.
- The stop hotkey aborts cleanly at any step.
- If Feishu is not foreground or its identity changed, the flow aborts with a clear error rather than typing into another app.
- The action log shows each step, with no message content.
- The same flow passes on macOS.

### Test conversation

Feishu is logged in on both machines. Acceptance runs against a dedicated test group or self-chat — never a real counterpart. The report content is supplied per run; nothing is attached.

### Qoder smoke test

The earlier Report Issue flow is retained as a secondary smoke test: it exercises a different Electron app and a form submit with a generated feedback code. It is not the milestone.

## 17. Milestones

| Milestone | Deliverable |
|---|---|
| **M0a — Windows spike** | Throwaway: list windows, capture Feishu, measure UIA coverage (chat list, message text, input box, Send), paste and send in a test conversation. Proves accessibility coverage. |
| **M0b — macOS spike** | The same measurement on the Mac, using AX + ScreenCaptureKit. Run before the protocol is frozen; both spikes inform the node vocabulary. |
| **M1 — Skeleton** | Workspace, shared protocol schema and types, MCP server + Windows host, token IPC, `status`/`windows`/`select`/`snapshot`/`focus`, tray, stop hotkey, per-app authorization, audit log. |
| **M2 — Core actions** | `click`/`type` (paste default)/`key`/`scroll`/`find`/`wait`/`read_text`, `computer_batch`, stale-target and focus guards, coordinate spaces. |
| **M3 — Feishu flow** | End-to-end reply on Windows: open, read, compose, paste, send, verify; acceptance test; authorization policy. |
| **M4 — Complete surface** | `drag`/`hover`/`move`/`window`/`clipboard`/`launch`/OCR/multi-monitor, config reload, packaging and install scripts. |
| **M5 — macOS parity** | Swift host implementing the same protocol; acceptance suite passes on macOS; Feishu flow passes on macOS. |
| **v2 — Background** | Isolated desktop/session or VM operation; Linux; app adapters; recording/replay. |

M0a and M0b are spikes and stay throwaway. M1+ is the real build. M0b and M5 run on the MacBook via Claude Code; Xcode is installed and the Mac runs macOS 26.6.1 on Apple silicon.

## 18. Future work

- **Background mode:** run the host in a VM or second desktop session so the agent works without taking over the user's desktop. This is v2 and the biggest UX upgrade.
- **Linux host:** the third platform, using AT-SPI or a Wayland-appropriate stack.
- **App adapters:** per-app plugins (Feishu, Cursor, Slack) that expose richer semantics than generic accessibility.
- **Recording/replay:** capture a human demonstration and turn it into a reusable skill.
- **Multi-client:** expose the same host to other MCP clients and to Claude Desktop.
- **Domain allowlist for browser-adjacent work:** reuse Kennect instead of pixel-driving Chrome.
- **Element memory:** stable identities across snapshots for long flows.
- **Unattended triggers:** watch for new messages and act without a user prompt — deliberately out of scope until permissions and policy mature.

## 19. Decisions

### Yours (from the conversation)

- Personal development tool, not Butterbase.
- Windows **and** macOS are v1; the agent works on whichever machine it runs on.
- Foreground control in v1; background moves to v2, after the MVP.
- Kennect-shaped MCP server + native hosts, one shared protocol.
- Flagship: replying to Feishu messages, end to end. Qoder is a smoke test.
- The user's instruction authorizes the action; no interactive approval for what was asked.
- Batched tool calls are expected; input is serialized, read-only work may be concurrent.
- Paste is the normal way text enters an app.
- Xcode and Claude Code are available on the MacBook; macOS 26.6.1 on Apple silicon; Feishu is logged in on both machines.
- Approvals are pre-cleared: Claude Code runs with skipped permissions most of the time, and the host pre-authorizes named apps. The host's per-app prompt remains for OS-related moments, where visibility is welcome.

### Mine (flag these if you disagree)

1. **TypeScript MCP server + platform-neutral protocol, C#/.NET 10 Windows host, Swift macOS host.** Native APIs where fidelity matters; one server everywhere.
2. **.NET 10 instead of .NET 8.** The SDK is installed and it is the current LTS; no reason to install an older runtime.
3. **Named pipe on Windows, Unix domain socket on macOS, plus a per-session token** — not Chrome native messaging. Works without Chrome and survives MCP restarts.
4. **Hybrid perception, accessibility-first.** Vision-only would be simpler but slower and less reliable; accessibility-only fails on canvas content.
5. **Opaque platform-neutral IDs and generic node/action vocabulary.** Keeps one tool surface and one acceptance suite.
6. **Per-app first-use authorization with explicit pre-authorization for named apps; consequential gate off by default.** Matches "the instruction is the authorization" while keeping OS-level visibility.
7. **On-demand host start; the tray/menu bar persists once running.**
8. **Default image long edge 1568 px**, with opt-in 2576 for detail.
9. **macOS 14+ deployment target**, stable self-signed identity for TCC grants.
10. **Window identity excludes the title; coordinate actions require fresh geometry.** Title changes (unread counts) are normal and must not abort flows; geometry changes make coordinates unsafe.
11. **Mutating actions accept host-evaluated `expect` predicates.** The right window and the right conversation are asserted mechanically before consequential input, not only by the model.
12. **Working name Kennect Computer**, folder `Documents\kennect-computer`. Rename freely.

## 20. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Feishu's accessibility tree is thin (Electron) on one or both platforms | Semantic targeting fails | M0a/M0b measure it; vision fallback; coordinate mapping with verification |
| macOS TCC grants reset or are hard to obtain | Host cannot capture or control | Stable bundle identity, signed build, clear first-run guidance, `PERMISSION_MISSING` |
| Two hosts drift apart | Tool behaves differently per platform | Shared protocol schema, conformance tests, one acceptance suite run on both |
| DPI/scale mismatches | Clicks land off-target | Explicit coordinate spaces, scale returned with every image, tests at 150% and Retina |
| Foreground activation refused | Wrong-app typing | Verify foreground before input; fail closed |
| Model invents elements | Wrong clicks | Node IDs must come from the latest snapshot; verify after action |
| Long flows drift | Acting on changed UI | Re-snapshot at checkpoints; abort on identity change |
| Prompt injection on screen | Unintended actions | Treat screen as data; per-app authorization; optional gate; stop hotkey |
| Sending the wrong message | Irreversible social damage | Authorization comes from the instruction; pre-Send conversation assertion; stop hotkey; audit; acceptance only in test conversations |
| Maintaining two native hosts | Slower iteration | Keep the protocol small; no platform logic in the server; spikes before committing |
| macOS full-screen Spaces and activation policies | Focus failures | Detect and fail with `FOCUS_REFUSED`; document the limitation |

## 21. Acceptance test suite (v1)

Runs on Windows and macOS unless noted.

1. `computer_windows` lists Feishu, Notepad (Windows) / TextEdit (macOS), and Chrome with correct PIDs.
2. Snapshot returns a tree with at least 10 interactive nodes for Notepad/TextEdit and Feishu.
3. Click by node and by coordinates both open Notepad's File menu (Windows) or TextEdit's menu (macOS).
4. Paste text into Notepad/TextEdit; read it back; verify an exact match, including a CJK string.
5. Stale node ID after re-snapshot returns `STALE_SNAPSHOT`.
6. Focus refusal returns `FOCUS_REFUSED`, never types into another app.
7. Stop hotkey aborts a click/type sequence within 200 ms.
8. Locked workstation returns `DESKTOP_UNAVAILABLE`.
9. Excluded process never appears in snapshots.
10. `computer_batch` runs steps in order, stops at the first failure, and returns per-step results.
11. Feishu reply flow sends the instructed message to the test conversation and confirms it in the thread.
12. Audit log contains every action with target identity and result, and no message content.
13. Host restart clears the control lock and requires re-authorization.
14. macOS without Accessibility or Screen Recording returns `PERMISSION_MISSING` with the exact grant to enable.
15. Qoder Report Issue smoke test (optional) returns a feedback code matching the confirmation screen.
16. Moving or resizing the selected window after a snapshot makes coordinate actions return `STALE_SNAPSHOT` unless `space: "screen"` is set.
17. A failed `expect` predicate returns `EXPECTATION_FAILED` and performs no input.
18. A Feishu title change (unread count) does not invalidate the selected window or abort the flow.

## 22. Open questions for review

1. Should the host start at login, or only on demand from the MCP server? (Default: on demand.)
2. Is 7 days of metadata-only audit retention right?
3. Any apps that must be permanently excluded beyond the password-manager and OS-security defaults?
4. Should the default image long edge stay at 1568 px, or adapt to display scale?
