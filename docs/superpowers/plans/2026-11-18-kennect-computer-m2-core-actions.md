# Kennect Computer — M2 Core Actions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the agent safe desktop actions — click, type (paste by default), key, scroll, find, wait, read_text, and ordered batches — with stale-target and focus guards and explicit coordinate spaces.

**Architecture:** The MCP server validates action requests and sends one JSON-RPC request per action (or one batch) to the Windows host. The host resolves the selected window and a snapshot-generation node, maps coordinates through the stored image scale/sourceBounds, verifies foreground, then acts through UIA patterns first and SendInput/paste fallbacks. All input is serialized; the control token aborts sequences.

**Tech Stack:** Node 24 (>=20), TypeScript strict, `@modelcontextprotocol/sdk`, zod, vitest; .NET 10 (`net10.0-windows`), C#, FlaUI.UIA3, WinForms, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-18-m2-core-actions-design.md` (authority), with `DESIGN.md` §4, §6, §9, §11, §14, §21.

## Plan series

This is Plan 2 of 5. It produces working, testable software on its own.

1. **Plan 1 — M0 spikes + M1 skeleton (complete).**
2. **This plan — M2 core actions.** Shared action protocol, snapshot state/generation for actions, UIA node resolution, coordinate spaces, SendInput/clipboard primitives, expect, click/type/key/scroll/find/wait/read_text, host-side batch, dispatcher and MCP tools, owned-window acceptance.
3. **M3 — Feishu flow.** End-to-end reply, composition, pre-Send assertion, acceptance test.
4. **M4 — Complete surface.** drag/hover/move/window/clipboard/launch/OCR/multi-monitor, packaging.
5. **M5 — macOS parity.** Swift host on the same protocol and acceptance suite.

## Global Constraints

- Node 20+ (development machine: Node 24.13); TypeScript strict; npm workspaces.
- .NET 10 SDK; target `net10.0-windows`; nullable enabled; implicit usings enabled.
- Protocol v1: JSON-RPC 2.0 over the existing framed named pipe; camelCase payloads; native handles never cross the protocol.
- Existing shared protocol shapes (`WindowInfo`, `Node`, `Snapshot`, `ImagePayload`, `HostStatus`, `HelloResult`, `ERROR_CODES`) are frozen. M2 adds new types/methods only.
- Window IDs are opaque and never reused across host starts; node IDs are `n<generation>:<index>` and valid only for the snapshot generation that produced them.
- Selected window is the action target; actions do not retarget another window.
- Coordinate spaces: `image` (default; returned image pixel space) and `screen` (absolute physical pixels). Image-space actions require a snapshot with an image; a moved/resized window returns `STALE_SNAPSHOT` unless `space: "screen"`.
- The host client serializes every request, so a read-only call waits behind a long wait or input action. The stop hotkey (Ctrl+Alt+Shift+Esc) and tray Stop call `ControlSession.Stop()` and must abort a click/type sequence.
- `computer_stop` cannot preempt an in-flight request on the same MCP connection; the hotkey/tray are the preemption path.
- Error codes are the frozen list. `EXPECTATION_FAILED` and `STALE_SNAPSHOT` are the action guards; wait timeouts and stops use `ACTION_TIMEOUT`.
- Audit: `AuditEntry` gains optional `textLength` and `textSha256` for type actions; never text, message, title, or content.
- Automated tests launch no external apps and leave no windows behind (R28/R29). Real input tests use an owned, test-closed WinForms window.
- Commit messages carry no Claude/Anthropic attribution.
- Stop hotkey is `Ctrl+Alt+Shift+Esc`; default max read_text length is 10,000 chars; wait defaults are 10,000 ms timeout and 100 ms poll; batch max steps is 50.

## Review Focus

These are the failure modes most likely to hurt a user, and the task whose tests pin each one:

1. **A node ID from an older snapshot is acted on** — expected: `STALE_SNAPSHOT`, no input, selection intact. Pinned in Task 7.
2. **Foreground cannot be verified** — expected: `FOCUS_REFUSED`, no input reaches another app. Pinned in Tasks 7 and 8.
3. **Window moved/resized or DPI scale differs** — expected: correct image-to-physical mapping, or `STALE_SNAPSHOT`; never a click at the wrong physical point. Pinned in Task 4.
4. **Paste fails midway** — expected: the typed text is not left on the user's clipboard; the failure is reported. Pinned in Task 8.
5. **Stop during a type/batch sequence** — expected: the sequence aborts promptly, the host stays alive, and a batch reports the steps it completed. Pinned in Tasks 8, 12, and 16.

---

### Task 1: Shared M2 action protocol types and schemas

**Files:**
- Create: `packages/shared/src/actions.ts`
- Create: `packages/shared/src/action-schemas.ts`
- Modify: `packages/shared/package.json` (exports `./actions` and `./action-schemas`)
- Create: `packages/shared/test/actions.test.ts`

**Interfaces:**
- Consumes: `Node`, `Rect`, `Snapshot`, `WindowInfo` from `./protocol.js`.
- Produces: `CoordinateSpace = "image" | "screen"`; `MouseButton = "left" | "middle" | "right"`; `ScrollDirection = "up" | "down" | "left" | "right"`; `TypeMethod = "paste" | "value" | "keys"`; `Point { x, y }`; `ExpectPredicate` (`{kind:"foreground"}`, `{kind:"node", role?, name?, identifier?, focused?}`, `{kind:"text", role?, name?, identifier?, contains}`); `ClickRequest` (`nodeId?`, `x?`, `y?`, `space?`, `button?`, `count?`, `expect?`); `TypeRequest` (`text`, `method?`, `nodeId?`, `expect?`); `KeyRequest` (`keys`, `expect?`); `ScrollRequest` (`nodeId?`, `x?`, `y?`, `space?`, `direction`, `amount?`, `expect?`); `FindRequest` (`text?`, `role?`, `maxResults?`); `WaitCondition` (window/node/text discriminated union); `WaitRequest` (`condition`, `present?`, `timeoutMs?`, `pollMs?`); `ReadTextRequest` (`nodeId`, `maxLength?`); `ActionResult` (`window`, `action`, `method`, `needsObservation`); `FindResult` (`window`, `generation`, `matches: Node[]`, `scanned`, `truncated`); `ReadTextResult` (`window`, `nodeId`, `text`, `length`, `truncated`); `WaitResult` (`window?`, `condition`, `elapsedMs`); `ActionStep` (flattened discriminated union of click/type/key/scroll/wait/readText); `BatchRequest` (`steps`, `snapshotBefore?`, `snapshotAfter?`); `BatchStepResult` (`index`, `kind`, `ok`, `code?`, `message?`, `data?`); `BatchResult` (`steps`, `failedIndex?`, `snapshotBefore?`, `snapshotAfter?`, `warning?`).
- Produces zod: `ClickRequestSchema`, `TypeRequestSchema`, `KeyRequestSchema`, `ScrollRequestSchema`, `FindRequestSchema`, `WaitRequestSchema`, `ReadTextRequestSchema`, `ActionStepSchema`, `BatchRequestSchema`, `ExpectPredicateSchema`, `ActionResultSchema`, `FindResultSchema`, `ReadTextResultSchema`, `WaitResultSchema`, `BatchResultSchema`.

- [ ] **Step 1: Write the failing tests**

`packages/shared/test/actions.test.ts` must cover:
- `ClickRequestSchema` accepts `{ nodeId: "n1:2" }`; accepts `{ x: 10, y: 20 }`; rejects `{}` and `{ x: 10 }`; rejects an unknown field; `space` accepts only `image|screen`.
- `TypeRequestSchema` requires a non-empty `text`; `method` accepts only `paste|value|keys`.
- `WaitRequestSchema` accepts a window condition `{ condition: { kind: "window", process: "Feishu" } }` and a text condition; rejects an empty condition.
- `ActionStepSchema` parses a mixed `[{kind:"click", nodeId:"n1:2"}, {kind:"type", text:"hi"}, {kind:"wait", condition:{kind:"window", process:"Feishu"}}]`; rejects an unknown kind.
- `BatchRequestSchema` rejects an empty `steps` array and more than 50 steps.
- `ReadTextRequestSchema` rejects a missing `nodeId`.
- `ExpectPredicateSchema` accepts all three kinds and rejects `{ kind: "text" }` without `contains`.
- `ActionResultSchema` validates a result and `BatchResultSchema` carries the ordered steps, a failed index, and a warning.

- [ ] **Step 2: Run to verify failure**

Run: `npm test --workspace packages/shared`
Expected: FAIL — `../src/actions.js` / `action-schemas.js` do not exist.

- [ ] **Step 3: Implement the types and schemas**

Create `actions.ts` with the interfaces above. Create `action-schemas.ts` with strict zod objects and a discriminated union for `ActionStepSchema`; use `.refine` so a click/scroll has exactly one of `nodeId` or (`x` and `y`), and `ActionStepSchema` rejects unknown kinds. Add the two exports to `packages/shared/package.json`:

```json
"./actions": { "types": "./dist/actions.d.ts", "development": "./src/actions.ts", "import": "./dist/actions.js" },
"./action-schemas": { "types": "./dist/action-schemas.d.ts", "development": "./src/action-schemas.ts", "import": "./dist/action-schemas.js" }
```

- [ ] **Step 4: Run to verify pass**

Run: `npm test --workspace packages/shared` and `npm run typecheck --workspace packages/shared`
Expected: PASS; typecheck clean.

- [ ] **Step 5: Commit**

```bash
git add packages/shared
git commit -m "feat: add shared M2 action protocol"
```

### Task 2: Snapshot state and generations for actions

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Uia/SnapshotState.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Uia/SnapshotStore.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Uia/UiaSnapshotter.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Ipc/JsonRpcDispatcher.cs` (save with window bounds)
- Create: `packages/host-windows/KennectComputer.Host.Tests/SnapshotStoreTests.cs`

**Interfaces:**
- Consumes: `Snapshot`, `Rect`, `ImagePayload` from `Protocol`.
- Produces: `ImageMetadata(double Scale, Rect SourceBounds)`; `SnapshotState(int Generation, IReadOnlyDictionary<string, TreeModel> Nodes, Rect WindowBounds, ImageMetadata? Image)`; `SnapshotStore.Save(string windowId, Snapshot snapshot, IReadOnlyDictionary<string, TreeModel> nodeMap, Rect windowBounds)`; `SnapshotStore.TryGetState(string windowId, out SnapshotState? state)`; `SnapshotStore.TryGetNode(...)` remains; `UiaSnapshotter.NextGeneration() -> int` (monotonic, thread-safe) and `Capture` uses it.

- [ ] **Step 1: Write the failing tests**

`SnapshotStoreTests.cs`:
- Saving a snapshot with `Image = new ImagePayload { Data = "x", MimeType = "image/png", Scale = 1.5, SourceBounds = new Rect(10, 20, 800, 600) }` and `WindowBounds = new Rect(10, 20, 800, 600)` lets `TryGetState("w_1", out var state)` return generation, nodes, bounds, and `Image.Scale == 1.5`.
- `TryGetNode("w_1", "n1:1", out _)` succeeds; `"n2:1"` fails.
- A second save replaces the state; `TryGetNode` rejects the old generation.
- `SnapshotStore.Clear("w_1")` removes the state.
- `new UiaSnapshotter(fakeWalker).NextGeneration()` returns strictly increasing values, and two calls to `Capture` use them.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "SnapshotStore|Generation"`
Expected: FAIL — `SnapshotState`/`NextGeneration` do not exist.

- [ ] **Step 3: Implement the state and wire the dispatcher**

Add `SnapshotState.cs`; change `SnapshotStore.Save` to derive `ImageMetadata` from `snapshot.Image` when present and store `snapshot.Generation`, `nodeMap`, and `windowBounds`; keep `TryGetNode` generation checks. In `UiaSnapshotter`, add `public int NextGeneration() => Interlocked.Increment(ref _generation);` and replace the inline increment in `Capture` with `NextGeneration()`. In `JsonRpcDispatcher`, pass `current.Info.Bounds` (the post-walk re-resolved bounds) to `_snapshots.Save(...)`. Update the existing snapshot tests and any `Save` call sites.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "SnapshotStore|Generation|Snapshot"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: keep snapshot bounds and image metadata for actions"
```

### Task 3: UIA node resolution and semantic element actions

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Uia/IUiaNode.cs`
- Create: `packages/host-windows/KennectComputer.Host/Uia/IUiaNodeResolver.cs`
- Create: `packages/host-windows/KennectComputer.Host/Uia/NodeFingerprint.cs`
- Create: `packages/host-windows/KennectComputer.Host/Uia/WindowsUiaNodeResolver.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Uia/TreeModel.cs` (add `Value`)
- Modify: `packages/host-windows/KennectComputer.Host/Uia/WindowsUiaWalker.cs` (capture `Value`)
- Create: `packages/host-windows/KennectComputer.Host.Tests/NodeResolverTests.cs`

**Interfaces:**
- Consumes: `TreeModel`, `Rect`, `nint`.
- Produces: `TreeModel.Value` (`string?`, internal, not serialized); `IUiaNode` with `Role`, `Name`, `Identifier`, `Bounds`, `Enabled`, `Focused`, `Actions`, `TryInvoke()`, `TryToggle()`, `TrySelect()`, `TryExpand()`, `TryFocus()`, `TrySetValue(string)`, `TryGetText(out string?)`, `TryGetValue(out string?)`; `IUiaNodeResolver.Resolve(nint hwnd, TreeModel target, CancellationToken) -> IUiaNode?`; `NodeFingerprint.From(TreeModel)` / `Matches(TreeModel)` using role + name + identifier + a bounds hash (rounded to whole pixels); `WindowsUiaNodeResolver : IUiaNodeResolver` using FlaUI.UIA3 (find by identifier/name/role, then verify the fingerprint).
- `WindowsUiaWalker` captures UIA Text/Value into `TreeModel.Value`; the slicer and protocol `Node` ignore it.

- [ ] **Step 1: Write the failing tests**

`NodeResolverTests.cs`:
- A `FakeUiaNodeResolver` returns a node whose role/name/identifier/bounds match the target; `Resolve` returns it.
- Changing the role, name, identifier, or any bounds field makes `NodeFingerprint.Matches` false and `Resolve` return null.
- A target with no identifier matches a node with the same role + name + bounds.
- `IUiaNode.TrySetValue`/`TryGetText` are exercised through a fake node used by later action tests; add a test that `TryGetText` returns the configured text.
- `TreeModel.Value` round-trips in a constructed model (no serialization).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "NodeResolver|Fingerprint"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the resolver and the FlaUI wrapper**

Add `Value` to `TreeModel`; in `WindowsUiaWalker`, read the value/text property into it. Implement `NodeFingerprint` and `WindowsUiaNodeResolver` (attach to the window, find the candidate by identifier when present, else by name/role, then compare the fingerprint; return null on any mismatch). Implement `WindowsUiaNode` over the FlaUI element with the pattern methods. Do not change the snapshot `Node` shape.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "NodeResolver|Fingerprint|Snapshot"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: resolve snapshot nodes to live UIA elements"
```

### Task 4: Coordinate mapper

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Input/CoordinateMapper.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/CoordinateMapperTests.cs`

**Interfaces:**
- Consumes: `SnapshotState`, `WindowInfo`, `Rect`, `ErrorCode`.
- Produces: `CoordinateMapResult { Point? Point, ErrorCode? Error }`; `CoordinateMapper.Map(SnapshotState state, WindowInfo currentWindow, double x, double y, CoordinateSpace space) -> CoordinateMapResult`.
- Image space: require `state.Image` (else `INVALID_REQUEST`); require `currentWindow.Bounds == state.WindowBounds` (else `STALE_SNAPSHOT`); map `physicalX = state.Image.SourceBounds.X + x * state.Image.Scale`, `physicalY = ...`; clamp/normalize to ints. Screen space: require the point inside `currentWindow.Bounds` (else `INVALID_REQUEST`); no bounds-change check.

- [ ] **Step 1: Write the failing tests**

`CoordinateMapperTests.cs`:
- With `SourceBounds = (100, 50, 3000, 2000)`, `Scale = 1.913...` (3000/1568), `WindowBounds == current bounds`: image point `(10, 20)` maps to `(100 + 10*scale, 50 + 20*scale)`.
- Scale 1.5 and 2.0 map correctly; rounding is to the nearest physical pixel.
- A changed current window bounds with image space returns `STALE_SNAPSHOT`.
- Image space with `state.Image == null` returns `INVALID_REQUEST`.
- Screen space at `(1500, 900)` inside the window succeeds even when bounds changed.
- Screen space outside the window returns `INVALID_REQUEST`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter CoordinateMapper`
Expected: FAIL.

- [ ] **Step 3: Implement the mapper**

Use `double` math and round to `int` with `Math.Round(value, MidpointRounding.AwayFromZero)`. Compare window bounds exactly (x, y, width, height).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter CoordinateMapper`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: map image-space coordinates to physical pixels"
```

### Task 5: Input sender and clipboard primitives

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Input/IInputSender.cs`
- Create: `packages/host-windows/KennectComputer.Host/Input/InputEvent.cs`
- Create: `packages/host-windows/KennectComputer.Host/Input/WindowsInputSender.cs`
- Create: `packages/host-windows/KennectComputer.Host/Input/IClipboard.cs`
- Create: `packages/host-windows/KennectComputer.Host/Input/WindowsClipboard.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/InputSenderTests.cs`

**Interfaces:**
- Produces: `IInputSender` with `MoveTo(int x, int y)`, `Click(MouseButton button, int count)`, `Scroll(int delta, bool horizontal)`, `KeyDown(ushort virtualKey)`, `KeyUp(ushort virtualKey)`, `UnicodeText(string text)`; `IInputApi.Send(IReadOnlyList<InputEvent> events)`; `InputEvent` record (`Kind`, `X`, `Y`, `Button`, `Delta`, `VirtualKey`, `Unicode`, `Flags`); `WindowsInputSender(IInputApi)`; `IClipboard { string? GetText(); void SetText(string text); }`; `WindowsClipboard : IClipboard` (Win32 clipboard via `OpenClipboard/GetClipboardData/SetClipboardData`, no STA requirement).
- Define host enums `MouseButton` and `CoordinateSpace` in `Input/InputEvent.cs` (or `CoordinateMapper.cs`) for Tasks 4–9; Task 13's request DTO parser maps the shared strings to these enums.

- [ ] **Step 1: Write the failing tests**

`InputSenderTests.cs` with a `FakeInputApi`:
- `MoveTo(10, 20)` sends one move event at absolute physical coordinates.
- `Click(MouseButton.Left, 2)` sends move + left-down + left-up twice (or a double-click sequence with the correct event order).
- `Scroll(-120, false)` sends one vertical wheel event with delta -120.
- `KeyDown(0x11)`/`KeyUp(0x11)` send the correct virtual key events.
- `UnicodeText("漢a")` sends one unicode event per UTF-16 code unit with the unicode flag.
- A fake `IClipboard` stores and returns text; no test uses the real clipboard.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter InputSender`
Expected: FAIL.

- [ ] **Step 3: Implement the Windows sender and clipboard**

Map `IInputSender` calls to `INPUT` records and call `IInputApi.Send`; `WindowsInputSender` uses `SendInput`. Use `System.Windows.Forms.Clipboard` only if it can run on the calling thread; otherwise use the Win32 clipboard APIs in `WindowsClipboard`. Keep the test seam (`IInputApi`) so unit tests never inject input.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter InputSender`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: add SendInput and clipboard primitives"
```

### Task 6: Expect evaluator

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Actions/ExpectEvaluator.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/ExpectEvaluatorTests.cs`

**Interfaces:**
- Consumes: `IUiaWalker`, `IForegroundApi`, `TreeModel`, `ExpectPredicate`.
- Produces: `ExpectResult { bool Ok; string? FailedPredicate }`; `ExpectEvaluator(IUiaWalker walker, IForegroundApi foreground)`; `Task<ExpectResult> EvaluateAsync(nint hwnd, IReadOnlyList<ExpectPredicate> predicates, CancellationToken)`.
- A `foreground` predicate passes when `foreground.GetForegroundWindow() == hwnd`.
- A `node` predicate passes when a fresh walk contains a node matching the supplied role/name/identifier and, when `focused` is set, the focused flag matches.
- A `text` predicate passes when a matching node's `TreeModel.Value` contains the string (ordinal, case-insensitive).
- An empty predicate list passes.

- [ ] **Step 1: Write the failing tests**

`ExpectEvaluatorTests.cs`:
- Foreground pass/fail.
- Node exists by role/name/identifier; focused true/false.
- Text contains a CJK substring in `Value`.
- The first failing predicate is named in `FailedPredicate`.
- A fake walker is used; no desktop.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter ExpectEvaluator`
Expected: FAIL.

- [ ] **Step 3: Implement the evaluator**

Walk once per `EvaluateAsync` call (fresh read); match nodes by role/name/identifier; use `TreeModel.Value` for text. Return the first failure's name (`foreground`, `node:<name>`, `text:<contains>`).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter ExpectEvaluator`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: evaluate pre-action expectations"
```

### Task 7: Click action

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Actions/ClickAction.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/ClickActionTests.cs`

**Interfaces:**
- Consumes: `IUiaNodeResolver`, `ForegroundService`, `IInputSender`, `SnapshotStore`, `SelectionStore`, `WindowRegistry`, `CoordinateMapper`, `ExpectEvaluator`, `ClickRequest`, `ActionResult`.
- Produces: `ClickAction(...)`; `Task<ActionResult> ExecuteAsync(ClickRequest request, CancellationToken cancellationToken)`.
- Flow: resolve the selected window; expect; if `nodeId`, get the `SnapshotState` and node (generation check), resolve the live node (`null` → `STALE_SNAPSHOT`), focus the window, re-check foreground, try semantic actions (`Invoke`/`Toggle`/`Select`/`Expand`), else click the live bounds center; if coordinates, map (image/screen), focus, click at the point. Return `method` and `needsObservation: true`.

- [ ] **Step 1: Write the failing tests**

`ClickActionTests.cs` with fakes:
- Semantic click invokes `TryInvoke` and sends no mouse event.
- A node with no supported pattern clicks its live bounds center.
- Coordinate image-space click maps through the stored scale and sends the physical point.
- A stale node generation returns `STALE_SNAPSHOT` and sends no input.
- A node whose live fingerprint no longer matches returns `STALE_SNAPSHOT`.
- A moved window with image-space coordinates returns `STALE_SNAPSHOT`.
- `ForegroundService` failure returns `FOCUS_REFUSED` and sends no input.
- A failed `expect` returns `EXPECTATION_FAILED` and sends no input.
- A screen-space click bypasses the bounds-change check but still verifies foreground.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter ClickAction`
Expected: FAIL.

- [ ] **Step 3: Implement the click action**

Use the selected window's `ResolvedWindow` and current `WindowInfo`. Always evaluate expect before input. For node clicks, re-resolve the live `IUiaNode` and use its current bounds for the fallback center. For coordinates, call `CoordinateMapper.Map`. Check `cancellationToken.ThrowIfCancellationRequested()` before each input.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter ClickAction`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: click nodes and coordinates"
```

### Task 8: Type action

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Actions/TypeAction.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/TypeActionTests.cs`

**Interfaces:**
- Consumes: `IUiaNodeResolver`, `ForegroundService`, `IInputSender`, `IClipboard`, `SnapshotStore`, `SelectionStore`, `WindowRegistry`, `ExpectEvaluator`, `TypeRequest`, `ActionResult`.
- Produces: `TypeAction(...)`; `Task<ActionResult> ExecuteAsync(TypeRequest request, CancellationToken)`.
- Paste (`method: "paste"`, default): save the clipboard text; set the text; send Ctrl+V (`KeyDown(0x11)`, `KeyDown(0x56)`, `KeyUp(0x56)`, `KeyUp(0x11)`); wait (bounded, 2 s) for the target to consume the paste, polling its value/text when readable and otherwise draining the window's input queue; then restore the saved text in `finally`. Refuse with `INVALID_REQUEST` before writing when the clipboard cannot be read or holds non-text content (an image, files, or ANSI-only text). Value: resolve the node and `TrySetValue`; if unsupported, fall back to paste. Keys: `UnicodeText` per character. Optional `nodeId` focuses the live node first. Expect runs before input. A stale node returns `STALE_SNAPSHOT`; focus refusal returns `FOCUS_REFUSED`; no input on either failure.

- [ ] **Step 1: Write the failing tests**

`TypeActionTests.cs`:
- Paste records the old clipboard, sets the new text, sends the Ctrl+V chord, and restores the old text.
- If the paste input throws, the clipboard is still restored and the failure propagates.
- `method: "value"` calls `TrySetValue`; if it returns false, the paste path runs.
- `method: "keys"` sends Unicode text and no clipboard write.
- A node target focuses the live node before typing; a stale node sends no input.
- A failed `expect` sends no input and preserves the clipboard.
- A CJK string reaches the input/clipboard unchanged.
- The result `method` is `paste`, `value`, or `keys`; `needsObservation: true`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter TypeAction`
Expected: FAIL.

- [ ] **Step 3: Implement the type action**

Use `try/finally` around the clipboard save/set/send/restore. Check the cancellation token before setting the clipboard and before the chord. For `value`, require a resolved node and `TrySetValue`; fall back to paste on `false`. For `keys`, validate the text length (non-empty) and send Unicode per UTF-16 code unit.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter TypeAction`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: type text with paste, value, and keys"
```

### Task 9: Key and scroll actions

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Input/KeyParser.cs`
- Create: `packages/host-windows/KennectComputer.Host/Actions/KeyAction.cs`
- Create: `packages/host-windows/KennectComputer.Host/Actions/ScrollAction.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/KeyActionTests.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/ScrollActionTests.cs`

**Interfaces:**
- Produces: `KeyChord { IReadOnlyList<ushort> Modifiers; ushort Key; }`; `KeyParser.TryParse(string value, out KeyChord chord)` for `Enter`, `Tab`, `Escape`, `F1`–`F24`, arrows, `Home/End/PageUp/PageDown/Delete/Backspace/Space`, letters/digits, and chords `Ctrl+A`, `Ctrl+Shift+S`, `Alt+F4`, `Win+...`.
- `KeyAction(...).ExecuteAsync(KeyRequest, CancellationToken)`: expect, focus, key-down modifiers, key-down/key-up the key, key-up modifiers in reverse; no clipboard.
- `ScrollAction(...).ExecuteAsync(ScrollRequest, CancellationToken)`: node → live bounds center; coordinates → mapper; direction/amount → wheel delta (120 per notch; horizontal for left/right); expect/focus; `method: "wheel"`.

- [ ] **Step 1: Write the failing tests**

`KeyActionTests.cs`:
- `KeyParser` accepts the listed keys and rejects `"NotAKey"`, `"Ctrl+"`, `"Ctrl+NotAKey"`.
- `Ctrl+A` sends Ctrl down, A down, A up, Ctrl up, in that order.
- `Shift+Enter` sends Shift down, Enter down, Enter up, Shift up.
- A foreground refusal sends no key events.
- A failed expect sends no key events.

`ScrollActionTests.cs`:
- Down 3 at a node sends `Scroll(-360, false)` at the node's center (or the live node center).
- Up 1 at image-space coordinates maps through the mapper and sends `Scroll(120, false)`.
- Left 2 sends `Scroll(-240, true)`.
- A moved window with image-space coordinates returns `STALE_SNAPSHOT`.
- A failed expect sends no scroll.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "KeyAction|ScrollAction"`
Expected: FAIL.

- [ ] **Step 3: Implement the parser and actions**

Map names to virtual-key codes; parse modifiers case-insensitively (`Ctrl`/`Control`). Use `ForegroundService` and `ExpectEvaluator` before input. For scroll, `amount` defaults to 1 and is clamped to 1..100.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "KeyAction|ScrollAction"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: send keys and scroll gestures"
```

### Task 10: Find and read_text actions

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Actions/FindAction.cs`
- Create: `packages/host-windows/KennectComputer.Host/Actions/ReadTextAction.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/FindActionTests.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/ReadTextActionTests.cs`

**Interfaces:**
- `FindAction(IUiaWalker walker, UiaSnapshotter snapshotter, SnapshotStore store, SelectionStore selection, WindowRegistry registry)`: `ExecuteAsync(FindRequest, CancellationToken) -> FindResult`. A fresh walk of the selected window; match `text` (case-insensitive substring over name or value) and/or `role`; allocate `snapshotter.NextGeneration()`; slice with `SnapshotOptions { MaxDepth = 20, MaxNodes = 5000, Mode = A11y }`; save a `SnapshotState` with the window bounds and no image; return up to `maxResults` (default 50, max 500) matches with valid node IDs, `scanned`, and `truncated`.
- `ReadTextAction(IUiaNodeResolver resolver, SnapshotStore store, SelectionStore selection, WindowRegistry registry)`: `ExecuteAsync(ReadTextRequest, CancellationToken) -> ReadTextResult`. Resolve the node, `TryGetText`/`TryGetValue`, cap at `maxLength` (default 10,000, max 50,000), set `truncated`.
- Find invalidates the prior image metadata; coordinate actions after find require a new snapshot with an image.

- [ ] **Step 1: Write the failing tests**

`FindActionTests.cs`:
- A fake tree with nodes named "Send", "Search", and "Send later" returns both "Send" matches for `text: "send"` (case-insensitive).
- `role: "button"` filters by role.
- The returned generation is new and all matches have `n<generation>:` IDs; `store.TryGetState` has the same generation and no image.
- No matches returns an empty array and `ok`.
- `maxResults` caps results and reports `truncated`.
- A stale/absent selection returns `INVALID_REQUEST`.

`ReadTextActionTests.cs`:
- Returns the node's text and exact length.
- Caps at `maxLength` and sets `truncated`.
- A stale node returns `STALE_SNAPSHOT`.
- A node without a text/value pattern returns `INVALID_REQUEST` or an empty text result (whichever the interface specifies; assert the chosen behavior).
- CJK text round-trips unchanged.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "FindAction|ReadTextAction"`
Expected: FAIL.

- [ ] **Step 3: Implement the actions**

Find: walk, filter, slice with the new generation, save state. ReadText: resolve the live node, prefer `TryGetText`, fall back to `TryGetValue`, cap. Do not log or return window content in errors.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "FindAction|ReadTextAction"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: find nodes and read their text"
```

### Task 11: Wait action

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Actions/WaitAction.cs`
- Create: `packages/host-windows/KennectComputer.Host/Actions/IWaitClock.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/WaitActionTests.cs`

**Interfaces:**
- `IWaitClock { DateTimeOffset UtcNow { get; } Task Delay(TimeSpan delay, CancellationToken ct); }`; `SystemWaitClock` uses `DateTimeOffset.UtcNow`/`Task.Delay`.
- `WaitAction(IUiaWalker walker, WindowEnumerator windows, SelectionStore selection, WindowRegistry registry, IWaitClock clock)`: `ExecuteAsync(WaitRequest, CancellationToken) -> WaitResult`. Defaults: `present = true`, timeout 10,000 ms, poll 100 ms; max timeout 60,000 ms. Window condition enumerates windows by process/title; node/text conditions walk the selected window; text uses `TreeModel.Value`. A timeout returns `ACTION_TIMEOUT` with a message naming the condition; cancellation throws `OperationCanceledException`.

- [ ] **Step 1: Write the failing tests**

`WaitActionTests.cs` with a fake walker/clock:
- A node appears on the third poll; result `ok`, elapsed is the fake time.
- `present: false` returns when the node disappears.
- A window condition matches by process.
- A timeout at 10,000 ms returns `ACTION_TIMEOUT` and names the condition.
- Cancellation aborts promptly and does not throw a timeout.
- Text matching is case-insensitive and uses `Value`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter WaitAction`
Expected: FAIL.

- [ ] **Step 3: Implement the wait loop**

Poll until the deadline; check the cancellation token before each walk and delay; return the matching window when a window condition succeeds. Never send input.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter WaitAction`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: wait for windows, nodes, and text"
```

### Task 12: Batch action

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Actions/BatchAction.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/BatchActionTests.cs`

**Interfaces:**
- `BatchAction(ClickAction click, TypeAction type, KeyAction key, ScrollAction scroll, WaitAction wait, ReadTextAction readText, UiaSnapshotter snapshotter, SnapshotStore store, SelectionStore selection, WindowRegistry registry)`: `ExecuteAsync(BatchRequest, CancellationToken) -> BatchResult`.
- Execute steps in order, stopping at the first failure; each step result has `index`, `kind`, `ok`, `code?`, `message?`, `data?`. `failedIndex` is the zero-based failed step. `snapshotBefore`/`snapshotAfter` are a11y-only snapshots of the selected window (no image). `snapshotBefore` is returned only: saving it would replace the state the batch's own steps act on. `snapshotAfter` is saved for later actions. Snapshots are best-effort, and a failure is recorded in `BatchResult.Warning` without stopping the steps. Cancellation between steps stops the batch and returns the completed steps with `failedIndex` set to the next step.

- [ ] **Step 1: Write the failing tests**

`BatchActionTests.cs`:
- Three successful steps run in order; results are ordered 0,1,2.
- The second step fails; the third is not run; `failedIndex == 1`; results contain steps 0 and 1.
- `snapshotBefore`/`snapshotAfter` add snapshots with a11y nodes and no image.
- A stop token between steps stops the batch and reports the completed steps.
- An empty `steps` is rejected before reaching the batch service (schema test in Task 1).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter BatchAction`
Expected: FAIL.

- [ ] **Step 3: Implement the batch service**

Dispatch each step by kind to the existing action services. Catch action failures (map to the R18-style step result) and stop. Do not catch `OperationCanceledException` as a normal failure; propagate it after recording the completed steps. Use the same control token passed to each step.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter BatchAction`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: run ordered action batches"
```

### Task 13: Host dispatcher wiring for actions

**Files:**
- Modify: `packages/host-windows/KennectComputer.Host/Ipc/JsonRpcDispatcher.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Program.cs`
- Modify: `packages/host-windows/KennectComputer.Host.Tests/DispatcherTests.cs`
- Modify: `packages/host-windows/KennectComputer.Host.Tests/PipeServerTests.cs`

**Interfaces:**
- Host methods: existing `action` with `kind: "focus"` stays; add `kind: "click" | "type" | "key" | "scroll"`; add `find`, `wait`, `read_text`, `action.batch`.
- `action` params: `{ kind, nodeId?, x?, y?, space?, button?, count?, text?, method?, keys?, direction?, amount?, expect? }`; `find` params `{ text?, role?, maxResults? }`; `wait` params `{ condition, present?, timeoutMs?, pollMs? }`; `read_text` params `{ nodeId, maxLength? }`; `action.batch` params `{ steps, snapshotBefore?, snapshotAfter? }`.
- Dispatcher constructor gains the action services; update `Program.cs` and all constructor call sites (R3).
- All mutating operations use `_control.Token`; `OperationCanceledException` maps to `ACTION_TIMEOUT` with "The operation was stopped." (existing M1 mapping). Action errors map to their error codes; invalid params to `INVALID_REQUEST`.

- [ ] **Step 1: Write the failing tests**

`DispatcherTests.cs` additions:
- `action` with `kind: "click"` and a node calls the click service and returns the `ActionResult`.
- `action` without a selected window returns `INVALID_REQUEST`.
- `action` with a stale node returns `STALE_SNAPSHOT`.
- `find` returns matches with a generation; `read_text` returns the text; `wait` passes the condition and timeout.
- `action.batch` returns per-step results and the failed index.
- An invalid `kind` returns `INVALID_REQUEST`; a missing required field returns `INVALID_REQUEST`.
- Existing focus tests still pass.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter Dispatcher`
Expected: FAIL.

- [ ] **Step 3: Implement the dispatcher methods and wiring**

Parse the request DTOs strictly; require the selected window for all action/perception methods except the window `wait` condition; pass the control token. Keep `hello`/`status`/`windows.list`/`window.select`/`snapshot`/`stop` behavior. Wire the new services in `Program.cs` (resolver, input sender, clipboard, coordinate mapper, expect, actions, batch).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: dispatch M2 action methods"
```

### Task 14: MCP action tools and audit extension

**Files:**
- Create: `packages/mcp-server/src/tools/click.ts`
- Create: `packages/mcp-server/src/tools/type.ts`
- Create: `packages/mcp-server/src/tools/key.ts`
- Create: `packages/mcp-server/src/tools/scroll.ts`
- Modify: `packages/mcp-server/src/tools.ts`
- Modify: `packages/mcp-server/src/audit/log.ts` (optional `textLength`, `textSha256`)
- Create: `packages/mcp-server/test/click-tool.test.ts`
- Create: `packages/mcp-server/test/type-tool.test.ts`
- Create: `packages/mcp-server/test/key-tool.test.ts`
- Create: `packages/mcp-server/test/scroll-tool.test.ts`
- Modify: `packages/mcp-server/test/audit.test.ts`
- Modify: `packages/mcp-server/test/server.test.ts`

**Interfaces:**
- `clickTool(host, args)`, `typeTool(host, args)`, `keyTool(host, args)`, `scrollTool(host, args)` return `ToolResult<ActionResult>` and send `host.request("action", { kind, ...args }, HOST_REQUEST_TIMEOUT_MS)`.
- `AuditEntry` gains optional `textLength?: number` and `textSha256?: string`; `tools.ts` fills them for `computer_type` from the parsed `text` (UTF-8 byte length and SHA-256 hex), never the text itself.
- Tool schemas use the shared action schemas; invalid args are -32602. Register four tools with appropriate annotations (`click/type/key/scroll` are not read-only; idempotent false).
- `server.test.ts` expects 10 tools after this task.

- [ ] **Step 1: Write the failing tests**

- `clickTool` sends `action` with the click request and returns the envelope.
- `typeTool` sends `action` with the type request; the audit line contains `textLength` and `textSha256` and no text.
- `keyTool`/`scrollTool` send the expected request and map failures.
- Invalid args throw -32602.
- `server.test.ts` lists the four new tools.

- [ ] **Step 2: Run to verify failure**

Run: `npm test --workspace packages/mcp-server`
Expected: FAIL.

- [ ] **Step 3: Implement the tool clients and audit fields**

Reuse `failureFromError`/`ok`; validate the host payload with the shared `ActionResultSchema` from Task 1. Compute SHA-256 with `node:crypto` for type text. Keep the audit allow-list; extend it with the two optional fields only.

- [ ] **Step 4: Run to verify pass**

Run: `npm test --workspace packages/mcp-server`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/mcp-server
git commit -m "feat: add MCP click, type, key, and scroll tools"
```

### Task 15: MCP find, wait, read_text, and batch tools

**Files:**
- Create: `packages/mcp-server/src/tools/find.ts`
- Create: `packages/mcp-server/src/tools/wait.ts`
- Create: `packages/mcp-server/src/tools/read-text.ts`
- Create: `packages/mcp-server/src/tools/batch.ts`
- Modify: `packages/mcp-server/src/tools.ts`
- Create: `packages/mcp-server/test/find-tool.test.ts`
- Create: `packages/mcp-server/test/wait-tool.test.ts`
- Create: `packages/mcp-server/test/read-text-tool.test.ts`
- Create: `packages/mcp-server/test/batch-tool.test.ts`
- Modify: `packages/mcp-server/test/server.test.ts`

**Interfaces:**
- `findTool(host, args)` → `host.request("find", args, ...)` → `ToolResult<FindResult>`.
- `waitTool(host, args)` → `host.request("wait", args, ...)` → `ToolResult<WaitResult>`.
- `readTextTool(host, args)` → `host.request("read_text", args, ...)` → `ToolResult<ReadTextResult>`.
- `batchTool(host, args)` → `host.request("action.batch", args, ...)` → `ToolResult<BatchResult>`; the text content is a compact JSON of the envelope (step results only; no snapshot image).
- Register four tools; `server.test.ts` expects 14 tools. `find`/`wait`/`read_text` are read-only; `batch` is mutating. Invalid args → -32602; operational failures → `isError`.

- [ ] **Step 1: Write the failing tests**

- Each tool sends the right host method and validates the result schema.
- `batch` returns the per-step results and failed index; a failed step is an `ok:false` envelope with the step details.
- `read_text` text is returned but the audit line contains no text content.
- `server.test.ts` lists 14 tools with the expected annotations.

- [ ] **Step 2: Run to verify failure**

Run: `npm test --workspace packages/mcp-server`
Expected: FAIL.

- [ ] **Step 3: Implement the tools**

Reuse the tool patterns from Tasks 5/14. Register in `tools.ts`; keep the R18 envelope. Do not add text/content to the audit.

- [ ] **Step 4: Run to verify pass**

Run: `npm test --workspace packages/mcp-server`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/mcp-server
git commit -m "feat: add MCP find, wait, read_text, and batch tools"
```

### Task 16: Owned-window M2 integration and README

**Files:**
- Create: `packages/host-windows/KennectComputer.Host.Tests/M2InputTests.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/M2TestWindow.cs` (extract/reuse the owned window harness)
- Modify: `README.md`

**Interfaces:**
- `M2TestWindow` creates an owned WinForms window with a `Button` ("Action"), a `Label` ("Result") that changes when clicked, a `TextBox` ("Editor"), a `ListBox`/scrollable panel, and accessible names. It shows on an STA thread, focuses itself for input, and closes/joins deterministically.
- `M2InputTests` uses the real `WindowsInputSender`, `WindowsClipboard`, and `WindowsUiaNodeResolver` against `M2TestWindow`:
  - Click the button by node; the label changes to "clicked".
  - Click the button by image-space coordinates from a snapshot; the label changes.
  - Paste "Kennect 汉字 123" into the editor; `read_text` returns exactly that string.
  - `method: "keys"` types a short ASCII string; read_text matches.
  - Scroll the list; a scroll action does not throw and the list's scroll position changes (assert via UIA or the selected index).
  - A stale node after a second snapshot returns `STALE_SNAPSHOT` and does not click.
  - A failed `expect` returns `EXPECTATION_FAILED` and does not click.
  - A batch [click, type, read_text] returns ordered results; a batch with a failing middle step reports `failedIndex`.
- The tests must not launch external apps; they close the window and join its thread.

- [ ] **Step 1: Write the failing integration tests**

Add `M2InputTests.cs` and the window harness. Run them before the final wiring if possible; they will fail until Tasks 13–15 are complete.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter M2Input`
Expected: FAIL for the missing integration wiring.

- [ ] **Step 3: Wire the integration test and update README**

Instantiate the real services with the owned window handle (bypassing the pipe, as M1's snapshot integration does) and exercise the actions. Update README with the M2 tools, image/screen coordinate spaces, `expect`, batch semantics, audit text length/hash, and the manual live M2 acceptance (select a user-opened window, click/type/read_text, hotkey during a type/batch).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests` and `npm test` and `npm run typecheck`
Expected: PASS; host integration tests are green; no external app was launched.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows README.md
git commit -m "test: add owned-window M2 integration and docs"
```

## Self-Review

**Spec coverage:** §4 actions → Tasks 7–15; §6 semantic/text/coordinates/stale/expect/focus → Tasks 3–9; §9 reliability → Tasks 4, 6–8, 12; §11 IPC → Task 13; §14 audit → Task 14; §21 acceptance subset → Task 16. The M2 milestone row is fully covered; M3/M4/M5 remain out of scope.

**Type consistency:** `SnapshotState`, `ImageMetadata`, `IUiaNode`, `IUiaNodeResolver`, `IInputSender`, `IClipboard`, `ExpectEvaluator`, `CoordinateMapper`, `ActionStep`, `BatchResult` are defined once and referenced by later tasks. Host enums are introduced in Tasks 4–5; the C# request/response DTOs are introduced and consumed in Task 13.

**Step scan:** Every task has a failing test, a run-to-fail step, a minimal implementation, a run-to-pass step, and a commit. Test values (scale 1.5/2.0, 3000/1568, 10,000-char cap, 10,000 ms timeout, 100 ms poll, 50-step cap, 120 wheel delta, 50 max find results) are exact.

**Review Focus:** Each of the five failure modes has a named test in its owning task (Task 4 coordinates, Task 7 stale/focus, Task 8 clipboard/stop, Task 12 batch, Task 16 integration).
