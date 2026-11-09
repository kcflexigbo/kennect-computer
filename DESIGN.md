# Kennect Computer — Design & Features

**Status:** Draft for review
**Date:** 2026-11-05
**Owner:** Kenneth Igbo
**Scope:** Personal development tool — not a Butterbase product
**Working name:** Kennect Computer (sibling to the Kennect browser MCP server)

---

## 1. Intent and why

Today the agent can see and act on Chrome through the Kennect browser MCP server. Every task that leaves the browser — Qoder, Cursor, native Windows apps, installers, GUIs with no API — falls back to the human.

Kennect Computer gives the agent the same kind of control over the **whole Windows desktop**: list windows, see the screen, understand what is on it, and click/type into any app. It follows the same architecture as the browser tool: a local MCP server that the agent calls, backed by a local bridge that does the OS work.

The first proof is concrete: **drive Qoder's Report Issue flow end to end** — open the app, find the button, read the form, fill it, submit, and return the generated feedback code.

### Why now

- Qoder support explicitly requires an in-app report and a feedback code; there is no API for it.
- Claude Code's own computer-use MCP server is macOS-only; on Windows there is no equivalent.
- The Kennect pattern is proven: local MCP server + local bridge + per-target scoping + semantic actions with screenshot fallback.

---

## 2. Goals, non-goals, anti-goals

### Goals

1. **See:** capture a single window (or monitor) and return it to the model, with a compact accessibility outline alongside the image.
2. **Understand:** enough structure (window identity, focused control, modal state, interactive elements) that the model can orient without guessing from pixels alone.
3. **Act:** semantic actions first (click a named control, set a value), coordinate actions as fallback; type, key, scroll, drag, focus.
4. **Recover:** fresh observation before/after actions, stale-target detection, wrong-window guards, clear errors.
5. **Stay interruptible:** a global stop hotkey and a visible "agent has control" indicator.
6. **Match Kennect's ergonomics:** same tool style, per-target selection, explicit authorization for consequential actions.

### Non-goals (v1)

- Not a multi-tenant or cloud service.
- Not macOS/Linux yet.
- Not an unattended scheduler or workflow engine.
- Not background/isolated-desktop operation (foreground control is accepted).
- Not a replacement for Kennect's browser tools; Chrome work stays on the browser MCP.
- No password-manager, banking, Windows Security, or UAC automation.
- No credential storage.

### Anti-goals (would make this a failure)

- A coordinate-only clicker that breaks when the app moves or scales.
- An agent that can hijack the mouse with no reliable way to stop it.
- Acting on the wrong window because the target changed between snapshot and click.
- Silently submitting forms, sending messages, or deleting things.
- A tool that requires the user to keep the app in a fixed position or resolution.

---

## 3. Product shape

```
Claude Code (or another MCP client)
        │  MCP over stdio
        ▼
┌─────────────────────────┐
│ kennect-computer-mcp    │  TypeScript MCP server
│  • tool definitions     │  • window/session scoping
│  • output shaping       │  • stale-target checks
│  • policy/allowlist     │  • audit log
└───────────┬─────────────┘
            │  JSON-RPC over named pipe (localhost only, token-authenticated)
            ▼
┌─────────────────────────┐
│ kennect-computer-host   │  .NET 8 Windows tray app / background process
│  • window enumeration   │  • UI Automation (FlaUI/UIA3)
│  • screen capture       │  • SendInput
│  • focus / DPI          │  • OCR (optional)
│  • stop hotkey          │  • approval prompts
└─────────────────────────┘
            │  Win32 / UIA / WinRT
            ▼
        Windows desktop
```

### Why split server and host

- The MCP server stays TypeScript, matching Kennect and Claude Code's ecosystem.
- The host is C#/.NET because Windows UI Automation, `SendInput`, DPI APIs, and WinRT capture are first-class there; Node bindings for these are weak or unmaintained.
- The split also gives a clean kill point: stopping the host releases all control immediately.

### Why a tray app

- Shows when the agent has control.
- Hosts the stop hotkey.
- Hosts per-app approval prompts.
- Survives MCP server restarts; one instance per logged-in user.

---

## 4. Capabilities and tool surface

Tool names use the `computer_` prefix. Node IDs are ephemeral and valid only for the snapshot that produced them.

### Session and targeting

| Tool | Purpose |
|---|---|
| `computer_status` | Host version, lock state, monitors, DPI, allowlist, whether the desktop is locked. |
| `computer_windows` | List top-level windows: id, title, process, PID, bounds, focused, minimized, visible. |
| `computer_select` | Select a window by id/title/process; returns a compact snapshot. Selection is explicit so later actions cannot drift to another window. |

### Perception

| Tool | Purpose |
|---|---|
| `computer_snapshot` | The primary observation. Returns a downscaled PNG plus an accessibility outline (role, name, automation id, bounds, enabled, focused, patterns) with stable node IDs. Params: window/monitor, `vision`/`a11y`/`both`, max depth, max nodes, image on/off. |
| `computer_find` | Search the selected window's accessibility tree by text/role; returns matching nodes without an image. |
| `computer_ocr` | Optional OCR of a region for canvas/non-UIA content. |
| `computer_read_text` | Read a control's text via TextPattern/ValuePattern. |
| `computer_wait` | Wait for a window, element, or text to appear/disappear, with timeout. |

### Actions

| Tool | Purpose |
|---|---|
| `computer_click` | Click a node (preferred) or coordinates; button, count, modifiers. |
| `computer_type` | Type text; optionally target a node (focus/ValuePattern) rather than the current focus. |
| `computer_key` | Key or chord (Enter, Ctrl+A, Alt+F4…). |
| `computer_scroll` | Scroll at a node/coordinate, direction and amount. |
| `computer_drag` | Drag from a node/coordinate to another. |
| `computer_hover` / `computer_move` | Pointer positioning. |
| `computer_focus` | Bring a window to the foreground or focus a control. |
| `computer_window` | Move, resize, minimize, restore, close (close is gated). |
| `computer_clipboard` | Read/write clipboard; explicit and audited. |
| `computer_launch` | Launch an app by path or known alias (gated). |
| `computer_stop` | Abort the current action and release control. |

### Output shape

Every action result includes:

- `ok` / `error` with a machine-readable code and a human message.
- The window identity that was acted on (handle + process + title hash).
- What changed: focused window/element after the action, when cheap.
- `needs_observation: true` when the caller should re-snapshot.

Images are returned as MCP image content. Default long edge 1568 px (cost/latency); `computer_snapshot` accepts up to 2576 px for detail work. Each result includes the scale factor and the source bounds so coordinates can be mapped back.

---

## 5. Perception model

The model should not have to guess from pixels when structure exists.

### Three layers

1. **Accessibility tree (primary)** — UI Automation. Gives roles, names, automation IDs, bounds, enabled/focused state, and supported patterns. This is the desktop equivalent of Kennect's accessibility snapshot.
2. **Screenshot (layout and visual state)** — confirms what the user sees, exposes canvas content, styling, error banners, and positions that UIA reports poorly.
3. **OCR (fallback)** — for canvas apps and controls whose text UIA does not expose. Windows.Media.Ocr keeps it local and fast; Tesseract is a fallback.

### The snapshot contract

`computer_snapshot` returns a compact state header plus the tree:

```
app: Qoder (Qoder.exe, pid 73760)
window: "Qoder" (handle 0x…, 1600x1000 at 0,0, dpi 150)
focused: [n42] button "Report Issue"
modal: none
nodes: 214 shown / 1432 total (depth 12, interactive + text)
```

Hidden/offscreen/disabled nodes are filtered by default. Interactive nodes and text are kept; purely decorative containers are collapsed. Each node carries a `nodeId` valid only for this snapshot.

### Why hybrid

- UIA-only is fast and precise but patchy — Electron/Chromium apps sometimes expose little until an accessibility client asks; custom-drawn UI exposes nothing.
- Vision-only is universal but slow, expensive, and prone to coordinate mistakes.
- The combination lets the model target semantically and verify visually, falling back to coordinates only when the tree is insufficient.

### Electron/Chromium notes

- Chromium enables accessibility on demand when a UIA client connects; Qoder should expose a usable tree.
- If the tree is empty, the host retries after focusing the window, and the snapshot falls back to vision-only with a warning.
- Webviews can appear as separate UIA subtrees; the host flattens them into the window outline.

---

## 6. Action model

### Semantic first

- `computer_click` with a `nodeId` uses InvokePattern, TogglePattern, SelectionItemPattern, or ExpandCollapsePattern where supported.
- `computer_type` with a `nodeId` uses ValuePattern or focuses the control first.
- If a pattern is unsupported, fall back to bounds-center clicking, then to coordinates.

### Coordinate mapping

- Host is per-monitor DPI aware (v2).
- Screenshots are captured at physical pixels, downscaled for the model, and returned with `scale` and `sourceBounds`.
- Coordinates accepted by tools are in the **returned image's pixel space** unless `space: "screen"` is set. The host maps back to physical pixels before injecting input.
- This mirrors the Kennect lesson: never mix screenshot pixels, CSS pixels, and device pixels silently.

### Stale targets

- Node IDs embed the snapshot generation. Acting on an ID from an older snapshot returns `STALE_SNAPSHOT` with instructions to re-observe.
- A node's identity is re-checked (automation id + name + role + bounds hash) before acting.
- If the window handle's process or title changed materially, the action is refused.

### Focus and foreground rules

- Before input, the host verifies the target window is foreground; if not, it calls a focus routine and re-verifies.
- Windows may refuse foreground changes (foreground lock); the host retries once, then returns `FOCUS_REFUSED` rather than typing into the wrong app.

### Consequential actions

- The MCP server flags actions with predictable consequences: Enter in a form, clicking controls named Send/Submit/Delete/Pay/Buy/Uninstall/Format, `computer_window close`, `computer_launch`, clipboard writes.
- Flagged actions trigger a cancellable countdown in the tray (default 3 s) unless the user has set `auto_approve` for that app. The agent is told the action was gated.
- The model is also instructed to ask the user in chat before consequential actions; the tray gate is the backstop, not the only control.

---

## 7. Session and window model

- **Control lock:** only one MCP session may control the desktop at a time. A named mutex plus a lock file; a stale lock is cleared when the owning process is gone.
- **Window selection:** `computer_select` fixes the target. Every subsequent action carries the selected window's identity; actions on a different window require an explicit re-select.
- **Per-app approval:** the first time a session controls a process, the tray asks Allow once / Allow for session / Deny. Denied apps are recorded for the session.
- **App identity:** process path + PID + window class + title, so an app restart invalidates stale approvals.
- **Multi-monitor:** snapshot accepts `monitor: all|primary|<id>`; default is the selected window's monitor.

---

## 8. Safety and permissions

### Hard rules

1. **Stop always works.** Global hotkey (default `Ctrl+Alt+Shift+Esc`) and tray Stop abort the current action and release input. The key is consumed.
2. **Locked desktop and secure desktop fail closed.** If the workstation is locked or UAC is showing, tools return `DESKTOP_UNAVAILABLE`.
3. **Sensitive windows are excluded by default.** Password managers, banking, Windows Security, and the host's own approval dialogs are never captured or controlled. Exclusion list is configurable.
4. **No credentials.** The tool does not read password fields, keychains, or browser credential stores. Typing a password is allowed only as explicit user-supplied text.
5. **Audit everything.** JSONL log of tool, target window, node/coordinates, text length (not content for fields marked sensitive), result, and duration.

### Prompt-injection posture

Screen content is untrusted data. Tool descriptions say so. The host never executes instructions found on screen; the model is the only actor, and its actions pass through the policy layer. This is a mitigation, not a guarantee — the stop hotkey and per-app approval are the real boundaries.

### Data handling

- Screenshots and trees stay local except for the images the MCP server returns to the model.
- No telemetry.
- Audit log retention configurable; default 7 days, rotated.

---

## 9. Reliability and error handling

| Failure | Behavior |
|---|---|
| Stale node ID | `STALE_SNAPSHOT`; caller re-snapshots. |
| Window gone | `WINDOW_GONE`; selection cleared. |
| Focus refused | One retry, then `FOCUS_REFUSED`. |
| UIA tree empty | Retry after focus; vision-only snapshot with warning. |
| App busy/hung | `APP_NOT_RESPONDING` after a 2 s probe. |
| Secure desktop / lock | `DESKTOP_UNAVAILABLE`, no input attempted. |
| Host disconnected | MCP server returns `HOST_UNAVAILABLE`; it respawns the host once. |
| Action timeout | Abort, re-snapshot, report partial state. |

Every mutating action is followed by a cheap verification (focused element, window title, or a caller-requested snapshot). The agent is responsible for the semantic check; the tool guarantees the mechanical one.

---

## 10. Architecture and repo layout

```
kennect-computer/
  DESIGN.md
  README.md
  package.json                  # workspace root
  packages/
    mcp-server/                 # TypeScript, stdio MCP
      src/
        index.ts
        tools/
        protocol/
        policy/
        audit/
    host/                       # .NET 8 WinExe
      KennectComputer.Host/
        Program.cs
        Windows/                # enumeration, focus, DPI
        Capture/                # WGC / PrintWindow / BitBlt
        Uia/                    # tree walking, patterns
        Input/                  # SendInput
        Ocr/                    # Windows.Media.Ocr
        Ipc/                    # named pipe server
        Tray/                   # status, approvals, stop
    shared/                     # JSON schema / TS types for the protocol
  config/
    default.json
  scripts/
    install-dev.ps1
    run-dev.ps1
    acceptance-qoder.md
```

### Runtime

- MCP server: Node 20+, launched by Claude Code over stdio.
- Host: .NET 8, self-contained single-file publish, current-user process (not a service) so it can see the interactive desktop and UIA.
- IPC: named pipe `\\.\pipe\kennect-computer-<user-sid>`, JSON-RPC 2.0, length-prefixed frames.
- Auth: 32-byte token in `%LOCALAPPDATA%\KennectComputer\session.token`, ACL to the current user; rotated per host start. The MCP server reads it; the host rejects unknown tokens.
- Single instance: named mutex; a second host starts the tray and exits.

### Why not native messaging

Chrome native messaging is tied to the browser. The desktop host should work with or without Chrome, and a named pipe is easier to debug, version, and keep alive across MCP restarts.

---

## 11. IPC protocol (v1)

JSON-RPC methods:

| Method | Direction | Notes |
|---|---|---|
| `hello` | server → host | token, protocol version, client name. |
| `status` | server → host | lock state, monitors, DPI, permissions. |
| `windows.list` | server → host | top-level windows. |
| `window.select` | server → host | returns identity + snapshot. |
| `snapshot` | server → host | mode, depth, node cap, image options. |
| `find` | server → host | text/role query. |
| `action` | server → host | discriminated union: click/type/key/scroll/drag/focus/window/clipboard. |
| `wait` | server → host | condition + timeout. |
| `stop` | server → host | abort. |
| `event` | host → server | `approval_required`, `window_changed`, `host_stopping`. |

Events are fire-and-forget; the server turns `approval_required` into a tool error or a pending state, never a silent block.

---

## 12. Windows implementation notes

| Concern | Choice |
|---|---|
| Window enumeration | `EnumWindows` + `GetWindowThreadProcessId`; filter cloaked/tool windows via DWM (`DWMWA_CLOAKED`). |
| UIA | FlaUI.UIA3 (managed UIA3) with cache requests; tree walker limited by depth/node caps. |
| Capture | Window: `PrintWindow(PW_RENDERFULLCONTENT)` first, `Windows.Graphics.Capture` when available; monitor/desktop: `BitBlt` or WGC. Fallback `CopyFromScreen`. |
| Input | `SendInput` for mouse/keyboard; never `keybd_event`/`mouse_event`. |
| DPI | `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`; convert client/screen/image coordinates explicitly. |
| Focus | `SetForegroundWindow` with the `AttachThreadInput` workaround; verify foreground after. |
| OCR | `Windows.Media.Ocr` (local, language packs); optional Tesseract. |
| Tray | WinForms `NotifyIcon` or WPF; global hotkey via `RegisterHotKey`. |
| Packaging | `dotnet publish -r win-x64 --self-contained`; no installer required for v1. |

### Known limitations

- Some apps render black in `PrintWindow` when minimized; restore before capture.
- UIA can be slow on huge trees; the cache request and node caps are mandatory.
- Foreground lock can refuse focus changes; fail rather than type blindly.
- UAC and the lock screen cannot be automated by design.
- Electron webviews may expose duplicate subtrees; dedupe by bounds + automation id.

---

## 13. Configuration

`config/default.json`:

```json
{
  "host": {
    "pipeName": "kennect-computer",
    "startOnDemand": true
  },
  "control": {
    "stopHotkey": "Ctrl+Alt+Shift+Esc",
    "approvalMode": "per-app",
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
      "WindowsSecurity.exe", "LogonUI.exe"
    ],
    "excludedTitles": ["*password*", "*bank*"],
    "auditRetentionDays": 7
  },
  "apps": {
    "Qoder": { "autoApprove": false }
  }
}
```

Config is per-user; the host watches the file and reloads non-structural changes.

---

## 14. Logging and privacy

- Audit log: `%LOCALAPPDATA%\KennectComputer\audit\YYYY-MM-DD.jsonl`.
- Fields: timestamp, session id, tool, app/window identity, node id or coordinates, text length/hash for typed text, result, duration, approval decision.
- Screenshots are not persisted by default; `computer_snapshot` can opt into saving to `%LOCALAPPDATA%\KennectComputer\snapshots` for debugging, with rotation.
- No network telemetry. The only outbound data is what the model receives as tool results.

---

## 15. Performance targets

| Operation | Target |
|---|---|
| `windows.list` | < 100 ms |
| UIA-only snapshot (typical app) | < 300 ms |
| Snapshot with image | < 700 ms |
| Click / type / key round trip | < 150 ms |
| OCR of a 1200×800 region | < 500 ms |
| Host cold start | < 2 s |

Image budget: default 1568 px long edge; the model can request zoomed crops instead of full-resolution screenshots.

---

## 16. First milestone: Qoder Report Issue

### Flow

1. `computer_windows` → find the Qoder window (process `Qoder`, title `Qoder`).
2. `computer_select` → focus and snapshot.
3. Locate **Report Issue**: UIA name first; if absent, vision coordinates from the snapshot.
4. `computer_click` → the Report Issue control.
5. `computer_snapshot` → confirm the form (title, description, submit).
6. Fill the title and description from the prepared report; attach nothing.
7. Submit.
8. Read the feedback code from the confirmation (TextPattern, clipboard, or OCR).
9. Return the code and a screenshot of the confirmation.

### Acceptance criteria

- The agent completes the flow without the user touching the mouse or keyboard.
- The returned code matches Qoder's visible confirmation.
- The stop hotkey aborts cleanly at any step.
- If Qoder is not the foreground window or its identity changed, the flow aborts with a clear error rather than typing into another app.
- The action log shows each step and the approval decisions.

### Test report content

Use the already-sent Qoder report: 403 Forbidden on `POST /apphub/api/v1/extensions/submissions`, publication `f2634c45-…`, request IDs `d93b1765-…`, `9fda482b-…`, `14e6b31a-…`.

---

## 17. Milestones

| Milestone | Deliverable |
|---|---|
| **M0 — Spike** | Throwaway: list windows, capture Qoder, UIA-find Report Issue, click, read the form. Proves UIA coverage. |
| **M1 — Skeleton** | MCP server + host + token IPC; `status`, `windows`, `select`, `snapshot`, `click`, `type`, `key`, `focus`; stop hotkey; allowlist. |
| **M2 — Qoder flow** | End-to-end Report Issue; code extraction; acceptance test; audit log. |
| **M3 — Usability** | `find`, `wait`, `scroll`, `drag`, `clipboard`, `read_text`; tray approvals; config reload. |
| **M4 — Resilience** | Stale-target guards, focus recovery, OCR, multi-monitor polish. |
| **M5 — Optional** | VM/background mode; macOS/Linux hosts; recording/replay. |

M0 is a spike and stays throwaway. M1+ is the real build.

---

## 18. Future work

- **Background mode:** run the host inside a VM or second Windows session so the agent works without taking over the user's desktop. This is the biggest UX upgrade.
- **App adapters:** per-app plugins (Qoder, Cursor, Slack) that expose richer semantics than generic UIA.
- **Recording/replay:** capture a human demonstration and turn it into a reusable skill.
- **Multi-client:** expose the same host to other MCP clients and to Claude Desktop.
- **Domain allowlist for browser-adjacent work:** reuse Kennect instead of pixel-driving Chrome.
- **Element memory:** stable identities across snapshots for long flows.

---

## 19. Decisions

### Yours (from the conversation)

- Personal development tool, not Butterbase.
- Windows-first.
- Kennect-shaped MCP server + local bridge.
- Foreground control is acceptable.
- First target: Qoder Report Issue, with real screen understanding.

### Mine (flag these if you disagree)

1. **TypeScript MCP server + C#/.NET host.** The host is where Windows fidelity matters; .NET gives UIA, SendInput, and capture without fragile Node bindings.
2. **Named pipe + per-session token** instead of Chrome native messaging. Works without Chrome and survives MCP restarts.
3. **Hybrid perception, UIA-first.** Vision-only would be simpler but slower and less reliable; UIA-only would fail on canvas content.
4. **Foreground-only v1, VM/background later.** Matches your answer and keeps the first build small.
5. **Default image long edge 1568 px**, with opt-in 2576 for detail.
6. **Empty allowlist by default**; first control of each app prompts. Safer than shipping a broad default.
7. **Consequential-action gate in the tray** (cancellable countdown) rather than relying only on the model asking.
8. **Working name Kennect Computer**, folder `Documents\kennect-computer`. Rename freely.

---

## 20. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Qoder's UIA tree is thin (Electron) | Semantic targeting fails | M0 spike measures it; vision fallback; coordinate mapping with verification |
| DPI/scaling mismatches | Clicks land off-target | Explicit coordinate spaces, scale returned with every image, acceptance tests at 150% scale |
| Foreground lock refuses focus | Wrong-app typing | Verify foreground before input; fail closed |
| Model invents elements | Wrong clicks | Node IDs must come from the latest snapshot; verify after action |
| Long flows drift | Acting on changed UI | Re-snapshot at checkpoints; abort on identity change |
| Prompt injection on screen | Unintended actions | Treat screen as data; tray gate; stop hotkey; allowlist |
| Maintenance of C# host | Slower iteration | Keep the protocol small; spike in Python only if needed |

---

## 21. Acceptance test suite (v1)

1. `computer_windows` lists Qoder, Notepad, and Chrome with correct PIDs.
2. Snapshot returns a tree with at least 10 interactive nodes for Notepad and Qoder.
3. Click by node and by coordinates both open Notepad's File menu.
4. Type into Notepad; read back the text; verify exact match.
5. Stale node ID after re-snapshot returns `STALE_SNAPSHOT`.
6. Focus refusal returns `FOCUS_REFUSED`, never types into another app.
7. Stop hotkey aborts a click/type sequence within 200 ms.
8. Locked workstation returns `DESKTOP_UNAVAILABLE`.
9. Excluded process never appears in snapshots.
10. Qoder Report Issue flow returns a feedback code matching the confirmation screen.
11. Audit log contains every action with target identity and result.
12. Host restart clears the control lock and requires re-approval.

---

## 22. Open questions for review

1. Do you want the host to start with Windows (tray autostart), or only on demand from the MCP server?
2. Should the tray approval default to "allow for session" after the first prompt, or ask every time?
3. Any apps that must be permanently excluded beyond the password-manager defaults?
4. Is 1568 px the right default image size for your monitor setup, or should it adapt to display scale?
5. Should the audit log ever store screenshot files, or metadata only?
