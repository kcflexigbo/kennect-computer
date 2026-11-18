# Kennect Computer — M2 Core Actions Design

**Status:** Approved (user, 2026-11-18)
**Date:** 2026-11-18
**Owner:** Kenneth Igbo
**Milestone:** M2 — Core actions
**Spec authority:** `DESIGN.md` (§4, §6, §9, §11, §14, §21)

## 1. Intent

M1 can see a window: list, select, snapshot, capture. M2 turns that into
acting on it. The agent gets click, type (paste by default), key, scroll,
find, wait, read_text, and ordered batches, with stale-target and focus guards
and explicit coordinate spaces. M3's Feishu reply flow depends on these.

## 2. Goals

- Semantic actions first: click a node through UIA patterns, type into a
  control through Value/Text patterns, fall back to bounds-center and
  coordinates when the app exposes no pattern.
- Paste-by-default text entry, reliable for CJK and Electron contenteditables.
- Stale-target safety: a node ID from an older snapshot, a changed window
  identity, or a moved window never receives input.
- Focus safety: input is sent only to the selected window after the host has
  verified it is foreground; otherwise `FOCUS_REFUSED`.
- Explicit coordinate spaces: image-pixel space by default, screen space on
  request; DPI/scale is never guessed.
- Ordered, host-side batches: one call, serial execution, stop at the first
  error, per-step results.
- `expect` predicates evaluated immediately before input; a failed predicate
  sends nothing and returns `EXPECTATION_FAILED`.
- The stop hotkey and `computer_stop` can abort an input sequence promptly.
- Audit records the tool, target, result, and text length/SHA-256 only — never
  the text or window content.

## 3. Non-goals

- Drag, hover, move, window management (move/resize/minimize/restore/close),
  clipboard read/write, app launch, OCR, multi-monitor targeting — M4.
- The end-to-end Feishu reply flow and its acceptance test — M3.
- macOS input parity — M5.
- The consequential-action countdown gate — config default is off; M3/M4.
- Background/isolated desktop operation — v2.

## 4. Constraints

- Protocol v1 stays compatible: new host methods and new shared types are
  additive; existing `WindowInfo`, `Node`, `Snapshot`, `HostStatus`, and
  error codes do not change shape.
- The selected window is the action target. Actions do not silently retarget
  another window; `computer_select` is the only selection path.
- One mouse and keyboard: the host serializes all mutating actions.
- The stop hotkey and `computer_stop` cancellation must abort a click/type
  sequence within the DESIGN §21 target (200 ms for the hotkey).
- Screen content is untrusted data; it is never written to the audit log.
- Automated tests launch no external apps and leave no windows behind
  (R28/R29). Real input is exercised against an owned, non-activating
  WinForms test window or a test-owned message window.
- Commit messages carry no Claude/Anthropic attribution.

## 5. Approaches considered

### A. Fingerprint re-resolution — chosen

At action time the host re-reads the live accessibility tree and finds the
stored node by its identity fingerprint: platform identifier (when present),
name, role, and a bounds hash. It acts only if the fingerprint matches.

- Pros: matches DESIGN §6 ("identity is re-checked"); works across tree
  changes; no COM lifetime problems; portable to the macOS host; keeps the
  existing generation-based `SnapshotStore`.
- Cons: one extra tree read per action; mitigated by UIA `FindFirst` and
  cached properties rather than a full walk when possible.

### B. Live UIA element cache — rejected

Store `AutomationElement` references with each node and re-validate.

- Pros: fastest lookup.
- Cons: COM references go stale across navigation/restart; memory-heavy;
  cannot be represented in the platform-neutral protocol; no macOS analogue.
  Not suitable as the generic action path.

### C. RuntimeId-first hybrid — rejected

Store the UIA RuntimeId and search by it, falling back to the fingerprint.

- Pros: fast when RuntimeIds are stable.
- Cons: provider-dependent; not portable; still needs the fingerprint
  fallback, so it adds a second code path without removing the first.

## 6. Decisions

### 6.1 Node resolution and the snapshot store

`SnapshotStore` keeps, per window: the snapshot generation, the node map
(`nodeId → TreeModel`), the snapshot's window bounds, and the image metadata
(`scale`, `sourceBounds`) when the snapshot included an image.

Before any node action the host:

1. Resolves the selected window through `WindowRegistry` (live handle, PID,
   process, class). A dead or replaced window clears the selection and returns
   `WINDOW_GONE`.
2. Parses the node ID's generation. A generation older than the stored
   snapshot returns `STALE_SNAPSHOT`.
3. Reads the live tree and matches by identifier + name + role + bounds hash.
   No match, or a material mismatch, returns `STALE_SNAPSHOT`. A title change
   alone never invalidates anything.
4. Uses a supported UIA pattern or falls back to bounds-center clicking.

The existing `NodeId`/`SnapshotStore` generation rules remain the authority.

### 6.2 Coordinate spaces

- `space` is `"image"` (default) or `"screen"`.
- Image-space coordinates are in the pixel space of the last snapshot's
  returned image. The host maps them back to physical pixels using the
  stored `scale` and `sourceBounds`.
- An image-space coordinate action requires a snapshot that included an image;
  otherwise it returns `INVALID_REQUEST` telling the caller to snapshot with
  `image: true`.
- If the window's bounds changed since that snapshot, image-space actions
  return `STALE_SNAPSHOT` unless `space: "screen"`.
- Screen-space coordinates are absolute physical pixels. The host still
  verifies the selected window is foreground and that the point lies within
  its bounds.
- Node bounds in the outline are physical pixels; node actions do not need
  coordinate conversion.

### 6.3 Click

- `nodeId` (preferred): invoke the supported activation action
  (Invoke/Toggle/SelectionItem/ExpandCollapse) for a plain left single click
  with no modifiers; otherwise click the node's bounds center after foreground
  verification. A non-empty modifier list, a non-left button, or a count other
  than one always takes the bounds-center mouse path.
- Coordinates: click at the mapped point with the requested button, count, and
  modifiers. Modifiers are pressed before the clicks and released in reverse
  in a `finally`.
- The bounds-center fallback requires non-empty live bounds and a center inside
  the verified-foreground window; otherwise it returns `STALE_SNAPSHOT`.
- The host verifies foreground before each click. Focus refusal returns
  `FOCUS_REFUSED` and sends no input.
- Click results report the target window and `needsObservation: true`.

### 6.4 Type

- `method: "paste"` (default): save the clipboard, set the text, send
  Ctrl+V, wait (bounded, 2 s) for the target to consume the paste, then restore
  the saved clipboard in a `finally`. A target whose content cannot be read
  falls back to a bounded input-queue drain. Paste fails with `INVALID_REQUEST`
  before writing when the clipboard cannot be read or holds non-text content
  (an image, copied files, or ANSI-only text), because a text-only restore
  would destroy it. The restore is best-effort; if another app holds the
  clipboard, the typed text may remain until that app releases it.
- `method: "value"`: set the control's ValuePattern where supported.
- `method: "keys"`: type character by character with SendInput
  `KEYEVENTF_UNICODE`; slower, fallback only.
- An optional `nodeId` focuses the control first. Without a node, text goes to
  the focused control of the selected window after foreground verification.
- Consequential/message-submitting actions are the user's instruction in M2;
  no interactive gate is added (the config gate remains M3/M4).
- Audit records text length and SHA-256, never the text.

### 6.5 Key

- A key or chord: `Enter`, `Tab`, `Escape`, `Ctrl+A`, `Ctrl+Shift+S`, `Alt+F4`,
  function keys, arrows, and single characters.
- The host parses modifiers and sends `SendInput` virtual-key events in order,
  releasing modifiers in reverse order. Unknown key names return
  `INVALID_REQUEST`.
- `key` is serialized with other input actions and honors cancellation.

### 6.6 Scroll

- Scroll at a node (bounds center) or coordinates; direction
  (`up`/`down`/`left`/`right`) and amount (wheel clicks or lines).
- Image-space coordinates follow §6.2; node targets use the node's bounds.
- Foreground verification applies.

### 6.7 Find

- Searches the selected window's live accessibility tree by text (substring,
  case-insensitive) and/or role.
- A fresh walk assigns a new snapshot generation; matches carry valid node IDs
  and are saved to `SnapshotStore`, so `computer_click` can act on them
  without taking a full snapshot.
- Returns matches without an image; includes role, name, identifier, bounds,
  states, and actions.
- No matches is a successful empty result, not an error.

### 6.8 Wait

- Conditions: a window exists (by process/title), a node exists by
  role/name/identifier, a node's text contains a string, or the reverse
  (disappears).
- Polls on a bounded interval with a deadline; honors cancellation.
- A timeout returns `ACTION_TIMEOUT` with a message identifying the condition;
  no new error code is introduced.
- Wait does not send input. The host client serializes every request, so a
  long wait blocks other host calls until it returns.

### 6.9 Read text

- Reads a control's text through TextPattern (document range) or ValuePattern,
  with a bounded length cap.
- Returns the text as untrusted content; audit records length/SHA-256 only.
- Missing or unsupported patterns return `INVALID_REQUEST` or a structured
  empty result as appropriate.

### 6.10 Expect

- Mutating actions accept an optional `expect` array. Predicates:
  - selected window is foreground;
  - a node exists by role/name/identifier (and optionally is focused);
  - a node's text contains a string.
- Predicates are evaluated against a fresh accessibility read immediately
  before input. The first failure returns `EXPECTATION_FAILED`, names the
  predicate, and sends no input.
- `expect` is evaluated once per action, and per step in a batch.

### 6.11 Batch

- The MCP tool accepts an ordered `steps` array of action steps
  (click/type/key/scroll; `wait` and `read_text` may be included as read-only
  steps) plus optional `snapshotBefore`/`snapshotAfter` flags.
- The host executes steps serially on one request, stopping at the first
  error. Results include each step's outcome, the failed step index, and the
  partial results.
- Optional before/after snapshots are accessibility-only (no image) and are
  returned alongside the step results.
- Batch steps share the control token; a stop cancels between steps and
  during waits. Mutating steps are never parallel.

### 6.12 Focus, stop, and concurrency

- Reuse `ForegroundService`: activate, verify, retry once, then
  `FOCUS_REFUSED`; never send input to an unverified window.
- Every input action checks the control token before each event/step; a stop
  aborts the sequence and returns `ACTION_TIMEOUT` with "The operation was
  stopped." (the existing M1 mapping).
- The host client serializes every request, so a read-only call does not
  overlap a long wait or an input action.
- The control session and single-root window rules from M1 remain.

### 6.13 Audit

- Every M2 tool is audited through the existing MCP choke point.
- `AuditEntry` gains optional `textLength` and `textSha256` for type actions.
  No text, message, title, or content is written.
- `nodeId` is recorded when the action targeted a node, and `x`/`y` when it
  targeted coordinates (DESIGN §14).
- The acted-on window ID and process name are recorded from the action result,
  including a batch's first step with a window.

### 6.14 Host IPC and MCP tools

- Host methods: `action` (discriminated union for click/type/key/scroll with
  `expect`), `find`, `wait`, `read_text`, `action.batch`. Existing `action`
  focus behavior remains compatible.
- MCP tools: `computer_click`, `computer_type`, `computer_key`,
  `computer_scroll`, `computer_find`, `computer_wait`,
  `computer_read_text`, `computer_batch`.
- All tool results use the R18 envelope; invalid arguments are JSON-RPC
  `-32602`; operational failures are `isError` results.
- Tool annotations: mutating tools are not read-only; find/wait/read_text are
  read-only. Batch is mutating.

## 7. Error mapping

| Condition | Code |
|---|---|
| Node/window from an older snapshot or changed bounds | `STALE_SNAPSHOT` |
| Window handle/process/class changed | `WINDOW_GONE`, selection cleared |
| Foreground cannot be set/verified | `FOCUS_REFUSED` |
| Expect predicate fails | `EXPECTATION_FAILED` |
| Wait deadline expires | `ACTION_TIMEOUT` |
| Stop cancels an action | `ACTION_TIMEOUT` ("The operation was stopped.") |
| Invalid key/coordinate/method/space | `INVALID_REQUEST` |
| Control-session conflict | `LOCKED_BY_OTHER_SESSION` |

## 8. Testing

- Host unit tests with fakes for the UIA walker, input API, clipboard,
  foreground API, and clock: node fingerprint matching, stale generations,
  bounds changes, coordinate mapping at 100%/150%/200%, expect failure with
  no input, focus refusal with no input, key parsing, batch ordering and
  stop-at-first-failure, wait timeout, read_text length caps.
- Owned WinForms integration: click a button and observe a label change;
  click by coordinates; type/paste a CJK string into a textbox and read it
  back; scroll a list; read_text; stop during a long type sequence. The test
  creates, focuses when needed, and closes its own window; no external apps.
- Dispatcher tests: generation/stale checks, selected-window enforcement,
  `STALE_SNAPSHOT` on moved windows, `FOCUS_REFUSED`, `EXPECTATION_FAILED`,
  batch per-step results.
- MCP tests: schemas, R18 envelopes, invalid arguments, tool registration,
  audit length/hash only, batch failure index.
- The full host, MCP, and shared suites plus typecheck must pass.

## 9. Acceptance for M2 (subset of DESIGN §21)

1. Click by node and by coordinates changes the owned test window's state.
2. Paste text into the owned textbox and read it back exactly, including CJK.
3. A stale node ID after a re-snapshot returns `STALE_SNAPSHOT`.
4. Focus refusal returns `FOCUS_REFUSED` and types nothing.
5. The stop hotkey aborts a click/type sequence; the host stays running.
6. `computer_batch` runs in order, stops at the first failure, and returns
   per-step results with the failed index.
7. Moving the window after a snapshot makes image-space coordinate actions
   return `STALE_SNAPSHOT` unless `space: "screen"` is set.
8. A failed `expect` predicate returns `EXPECTATION_FAILED` and sends no input.
9. A title change alone does not invalidate the selection or abort an action.

The live Feishu reply flow remains M3.

## 10. Risks and mitigations

- **UIA pattern coverage varies by app.** Fall back to bounds-center clicks;
  report the method used in the result.
- **Clipboard save/restore races.** Keep the text in a local variable, restore
  in `finally`, and never leave the typed text on the clipboard.
- **Foreground lock.** `ForegroundService` retries once and refuses; the tray
  and hotkey remain available.
- **Fingerprint collisions.** Match on identifier + name + role + bounds hash;
  a mismatch is `STALE_SNAPSHOT`, never a guess.
- **Batch cancellation.** Check the control token between steps and inside
  waits; a stopped batch returns the partial results it completed.
- **Input injection blocked in headless sessions.** Input tests use an owned
  interactive window; if the environment cannot inject, the test reports the
  limitation rather than silently passing.
