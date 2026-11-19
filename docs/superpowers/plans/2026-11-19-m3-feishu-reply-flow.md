# M3 Feishu Reply Flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status:** Complete (2026-11-19). Tasks 1–5 and 7 were executed; Task 6 (conditional generic fix) was not needed because the live acceptance passed.

**Goal:** Prove the Feishu reply flow end to end using only the generic M2 tools, leave a repeatable acceptance runbook and a live run record, and fix only the generic gaps the live run proves necessary.

**Architecture:** Acceptance-first. One read-only live-thread measurement feeds a runbook; an owned chat-like test window provides a deterministic dry run of the same choreography; the live run is the proof. No new product tool, no app adapter. If the live run requires app-specific logic, stop and design the orchestrator fallback instead of bending the generic tools.

**Tech Stack:** .NET 10 Windows host, FlaUI.UIA3, xUnit; TypeScript MCP server and shared schemas unchanged; Markdown runbook and acceptance record.

**Spec:** `docs/superpowers/specs/2026-11-19-m3-feishu-reply-flow-design.md`

## Global Constraints

- Live acceptance runs **only** in a dedicated test group or the user's own self-chat, never a real conversation. The user names the test chat and the exact test message before the run.
- Automated tests launch no external apps and leave no windows behind.
- Protocol v1 stays additive. No new host method, MCP tool, shared schema, or error code in this plan.
- The stop hotkey (`Ctrl+Alt+Shift+Esc`) stays available through every live step.
- The audit log contains every step and no message content.
- Feishu remains `preAuthorized` in `config/default.json`. No credentials are handled.
- Work on a new branch off `main`; do not commit to `main`. Commit messages carry no Claude/Anthropic attribution.
- Every task's verification includes the suites it touches; the final task runs the full host, MCP, and shared suites plus typecheck.

## Review Focus

- **Ambiguous chat title.** Two conversations with the same accessible name must stop before any click, not pick one. Task 3's `AmbiguousChatTitlesDoNotClick` pins this.
- **Wrong conversation at Send.** A Send whose conversation-header expectation does not match must send nothing. Task 3's `SendWithTheWrongHeaderSendsNothing` pins this.
- **Draft mismatch after paste.** The draft read-back must be exact, including CJK. Task 3's `FullReplyFlowSendsExactlyTheComposedText` pins this with `Kennect reply 汉字 123`.
- **Live-chat topology differs from the Assistant home.** The composer and Send control must be measured in a real test thread before the runbook is written. Task 1 pins this with a required measurement checklist.
- **Window moves or Send is ambiguous during the live run.** The runbook must abort rather than guess. Task 4's abort rules pin this.

---

### Task 1: Live-thread measurement (read-only)

**Files:**
- Create: `docs/spikes/2026-11-19-windows-feishu-live-thread.md`

**Interfaces:**
- Consumes: the running Kennect host and the MCP tools `computer_status`, `computer_windows`, `computer_select`, `computer_snapshot`, `computer_find`, `computer_click`, `computer_read_text`.
- Produces: the measured values and control descriptions that Task 4's runbook uses.

- [ ] **Step 1: Confirm the test conversation**

Ask the user to name the dedicated test conversation (the self-chat "Igbo Kenneth Chiagozie" or a test group). Record the exact title. Do not open or measure a real conversation.

- [ ] **Step 2: Select Feishu**

Call `computer_windows`; require exactly one window with process `Feishu`. Call `computer_select` with that `windowId`. Record the window bounds and the host version from `computer_status`.

- [ ] **Step 3: Open the test conversation**

Call `computer_find` with `{"text":"<TEST_CHAT>","role":"text"}`. Require exactly one match. Click that node. Do not type, send, or click any other conversation.

- [ ] **Step 4: Measure the live thread**

Call `computer_snapshot` with `{"windowId":"<FEISHU_WINDOW_ID>","mode":"both","image":true,"maxDepth":60,"maxNodes":3000}`. Record in the doc:

| Field | Required value |
|---|---|
| Window | bounds, scale, image `sourceBounds` |
| Chat list document | role, name, identifier, bounds |
| Test conversation node | role, name, identifier, bounds |
| Thread document | role, name, identifier, bounds |
| Message text sample | at least three `Text` node names from the thread |
| Message text depth/count | visible text-node count; whether `truncated` is true |
| Composer | role, name, identifier, bounds, actions |
| Send control | role, name (expected empty), identifier (if any), bounds, actions, position relative to the composer |
| Unnamed buttons | count and bounds of unnamed `Button` nodes near the composer row |

- [ ] **Step 5: Measure the text channels**

Call `computer_read_text` on the thread document's node id. Record `length` and whether the text includes the latest visible message. Call `computer_read_text` on the composer's node id and record the returned text (the current draft placeholder or empty).

- [ ] **Step 6: Write the measurement doc**

Create `docs/spikes/2026-11-19-windows-feishu-live-thread.md` with: environment table, the measurement table above, the Send-control identification rule (how the paper-plane button is distinguished from its neighbors), and the exact test conversation title. State explicitly that no message was sent and no input was typed.

- [ ] **Step 7: Verify and commit**

Re-read the doc against the checklist. Every required field is filled; no `TBD` remains. Then:

```bash
git add docs/spikes/2026-11-19-windows-feishu-live-thread.md
git commit -m "docs: measure the live Feishu test thread"
```

---

### Task 2: Extract the action harness for reuse

**Files:**
- Create: `packages/host-windows/KennectComputer.Host.Tests/ActionHarness.cs`
- Modify: `packages/host-windows/KennectComputer.Host.Tests/M2InputTests.cs`

**Interfaces:**
- Consumes: `M2TestWindow` (handle, bounds, raw window) and the action services already wired in the nested `Harness`.
- Produces:
  - `internal sealed class ActionHarness` with constructor `ActionHarness(nint handle, Func<Rect> readBounds, RawWindow rawWindow)`, the same public properties as today's nested `Harness` (`Windows`, `Registry`, `SelectedWindow`, `Selection`, `Snapshots`, `Walker`, `Resolver`, `Clipboard`, `Input`, `Snapshotter`, `Click`, `Type`, `Key`, `Scroll`, `ReadText`, `Find`, `Wait`, `Batch`), and `Snapshot(bool image)`.
  - `internal sealed class OwnedWindowApi : IWindowApi` with constructor `OwnedWindowApi(nint handle, Func<Rect> readBounds, RawWindow rawWindow)`.

- [ ] **Step 1: Run the M2 input baseline**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter M2Input`
Expected: PASS. This is a refactor; the existing tests are the characterization suite.

- [ ] **Step 2: Move the nested harness**

Move the nested `Harness` and `OwnedWindowApi` from `M2InputTests.cs` into `ActionHarness.cs`. Replace the harness constructor's `M2TestWindow` parameter with `(nint handle, Func<Rect> readBounds, RawWindow rawWindow)`; `Snapshot` uses `readBounds()` and `handle`. Keep the service wiring identical.

- [ ] **Step 3: Update M2InputTests**

Replace `new Harness(window)` with `new Harness(window.Handle, () => window.Bounds, window.ToRawWindow())` — or a small private helper that does the same. Behavior and test bodies do not change.

- [ ] **Step 4: Run the M2 input baseline again**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter M2Input`
Expected: PASS, same count as Step 1.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows/KennectComputer.Host.Tests/ActionHarness.cs packages/host-windows/KennectComputer.Host.Tests/M2InputTests.cs
git commit -m "refactor: share the owned-window action harness"
```

---

### Task 3: Owned chat-like window and dry-run flow tests

**Files:**
- Create: `packages/host-windows/KennectComputer.Host.Tests/M3ChatTestWindow.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/M3FlowTests.cs`
- Test: `packages/host-windows/KennectComputer.Host.Tests/M3FlowTests.cs`

**Interfaces:**
- Consumes: `OwnedWindowHost<TForm>` and `ActionHarness` from Task 2; `ClickAction`, `TypeAction`, `ReadTextAction` and their request records.
- Produces:
  - `internal sealed class M3ChatTestWindow` with `Start()`, `Handle`, `Bounds`, `ToRawWindow()`, `HeaderText`, `MessagesText`, `ComposerText`, `SendCount`, `Dispose()`, and constants `WindowTitle = "Kennect M3 Test"`, `TestChat = "Test Chat"`, `OtherChat = "Other Chat"`, `PreviousMessage = "Previous message from Alex"`, `ReplyText = "Kennect reply 汉字 123"`.
  - `M3FlowTests` in `[Collection(M2InputCollection.Name)]`.

- [ ] **Step 1: Write the failing tests**

Create `M3FlowTests.cs` with three tests:

```csharp
[Fact]
public async Task FullReplyFlowSendsExactlyTheComposedText()
{
    using var window = M3ChatTestWindow.Start();
    var harness = Harness(window);

    // Open the test chat, read the previous message, compose, read the draft
    // back exactly, then Send with the conversation-title expectation.
    // Assert the message pane gained ReplyText and the composer is empty.
}

[Fact]
public async Task SendWithTheWrongHeaderSendsNothing()
{
    using var window = M3ChatTestWindow.Start();
    var harness = Harness(window);

    // Open Other Chat, type ReplyText, click Send with an ExpectNode for
    // Test Chat. Assert ActionFailedException with ExpectationFailed, the
    // message pane is unchanged, and the composer still holds the draft.
}

[Fact]
public async Task AmbiguousChatTitlesDoNotClick()
{
    using var window = M3ChatTestWindow.Start(duplicateTestChat: true);
    var harness = Harness(window);

    // Two conversation entries are named Test Chat. A text click for
    // "Test Chat" must fail with InvalidRequest and the header must stay
    // "No chat".
}
```

The tests use `ActionHarness.Snapshot(image: false)`, `Assert.Single` by role/name for the conversation buttons, `ClickAction`, `TypeAction` with default paste, and `ReadTextAction`. The Send button is found as the single `button` node with an empty name.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter M3Flow`
Expected: FAIL — `M3ChatTestWindow` does not exist.

- [ ] **Step 3: Implement the chat window**

Create `M3ChatTestWindow.cs` on the `OwnedWindowHost<M3ChatForm>` pattern from `M2TestWindow` with `activate: true`:

- Left column: two `Button`s named `TestChat` and `OtherChat` (accessible names `"Test Chat"` and `"Other Chat"`); when `duplicateTestChat` is true, a third button also named `"Test Chat"` with distinct bounds.
- Header: `Label` with `AccessibleName = "Header"`, initial text `"No chat"`.
- Message pane: read-only multiline `TextBox` with `AccessibleName = "Messages"`, initial text `PreviousMessage` after a conversation opens.
- Composer: multiline `TextBox` with `AccessibleName = "Composer"`.
- Send: `Button` with `Text = ""`, `AccessibleName = ""`, placed immediately right of the composer; its click appends the composer text as a new line in `Messages`, clears `Composer`, and increments `SendCount`.
- Opening `Test Chat` sets the header to `"Test Chat"` and the message pane to `PreviousMessage`; opening `Other Chat` sets the header to `"Other Chat"` and leaves the pane unchanged.

Expose `HeaderText`, `MessagesText`, `ComposerText`, and `SendCount` through `Invoke`, like `M2TestWindow`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter M3Flow`
Expected: PASS, 3 tests. If the desktop cannot inject input, the test must report the environment error from the M2 harness, never pass silently.

- [ ] **Step 5: Run the full host suite**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests`
Expected: PASS, with the existing live-Notepad skip.

- [ ] **Step 6: Commit**

```bash
git add packages/host-windows/KennectComputer.Host.Tests/M3ChatTestWindow.cs packages/host-windows/KennectComputer.Host.Tests/M3FlowTests.cs
git commit -m "test: dry-run the Feishu reply flow on an owned chat window"
```

---

### Task 4: Acceptance runbook

**Files:**
- Create: `scripts/acceptance-feishu.md`

**Interfaces:**
- Consumes: the measurement from Task 1; the flow and safety rules from the design.
- Produces: the exact procedure Task 5 runs.

- [ ] **Step 1: Write the runbook**

Create `scripts/acceptance-feishu.md` with these sections, using the measured values from Task 1:

1. **Preconditions** — dedicated test chat named by the user, exact test message supplied by the user, Feishu open and logged in, host running from the branch, desktop unlocked, Feishu pre-authorized, stop hotkey known.
2. **Safety rules** — never run against a real chat; stop on an ambiguous title; stop if the selected window is not Feishu; stop if the Send control is ambiguous; no more than one message.
3. **Sequence** — numbered tool calls: `computer_status`; `computer_windows`; `computer_select`; `computer_find` for the test chat; click the single match; `computer_wait` for the thread header; `computer_read_text` on the thread; click the composer; `computer_type` paste with the test message; `computer_read_text` on the composer; `computer_click` Send with an `expect` for the test-chat title; `computer_read_text` on the thread; verify the message and the empty composer.
4. **Send identification** — the measured rule for choosing the paper-plane control, and the abort condition if more than one candidate matches.
5. **Stop-hotkey check** — press `Ctrl+Alt+Shift+Esc` during a non-send step; expect `ACTION_TIMEOUT` and a still-running host; then restart the flow.
6. **Abort rules** — any mismatch, focus refusal, ambiguous match, or missing Send stops the run with no further input.
7. **Evidence** — record the run in `docs/acceptance/2026-11-19-feishu-reply.md`; check the audit log for the run's entries and confirm no message content appears.

Use `<TEST_CHAT>` and `<TEST_MESSAGE>` as the only placeholders; the measurement supplies the rest. Every JSON example is a complete request body.

- [ ] **Step 2: Verify the runbook**

Check each numbered step against the measurement doc: the node roles/names, the Send rule, and the abort conditions match. No `TBD` remains.

- [ ] **Step 3: Commit**

```bash
git add scripts/acceptance-feishu.md
git commit -m "docs: add the Feishu reply acceptance runbook"
```

---

### Task 5: Live acceptance run

**Files:**
- Create: `docs/acceptance/2026-11-19-feishu-reply.md`

**Interfaces:**
- Consumes: the Task 4 runbook; the user's named test chat and exact test message.
- Produces: the live evidence and the go/no-go decision for Task 6.

- [ ] **Step 1: Confirm the run inputs**

Ask the user to confirm the dedicated test chat and the exact test message. Record both in the acceptance doc before touching Feishu. If either is missing, stop.

- [ ] **Step 2: Run the runbook**

Follow `scripts/acceptance-feishu.md` exactly. Send exactly one message. Keep the stop hotkey available. Do not improvise a different selector, chat, or message.

- [ ] **Step 3: Verify and record**

Record: date/time, host version, Feishu window and chat title, exact sent text, each step's result, the verification read-back, the stop-hotkey check, and the audit confirmation. Confirm the sent message appears in the thread and the composer is empty.

- [ ] **Step 4: Decide the gate**

- **Pass:** continue to Task 7.
- **Generic gap:** record the exact failing step and error in the acceptance doc, then do Task 6.
- **Needs app-specific logic:** stop. Record the finding and write an approach-2 design; do not add a Feishu tool in this plan.

- [ ] **Step 5: Commit**

```bash
git add docs/acceptance/2026-11-19-feishu-reply.md
git commit -m "docs: record the live Feishu reply acceptance"
```

---

### Task 6: Generic fix (conditional)

Run only when Task 5 records a generic gap.

**Files:**
- Modify: the owning host or MCP test and source files named by the gap.

**Interfaces:**
- Consumes: the failing step and error from Task 5.
- Produces: a minimal generic fix with a regression test, and a repeat of Task 5.

- [ ] **Step 1: Write the failing test**

Add a test to the owning suite that reproduces the exact gap. It must fail for the recorded reason before any fix.

- [ ] **Step 2: Run to verify failure**

Run the suite. Expected: the new test fails with the recorded behavior.

- [ ] **Step 3: Fix the generic path**

Change only the generic action/perception path. No Feishu-specific branch, no new tool, no protocol change.

- [ ] **Step 4: Run the affected suites**

Run the host suite, the MCP suite, or both, depending on the gap. Expected: PASS, including the new test.

- [ ] **Step 5: Commit and re-run the acceptance**

```bash
git add <the changed test and source files>
git commit -m "fix: <the generic gap>"
```

Then repeat Task 5's runbook against the same test chat and message.

---

### Task 7: Close-out docs and full verification

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: the measurement, the runbook, and the acceptance record.
- Produces: the M3 status and the manual acceptance pointer.

- [ ] **Step 1: Update README**

Change the current-milestone line to M3 and add a short "Manual Feishu acceptance" section pointing at `scripts/acceptance-feishu.md`, naming the dedicated-test-chat rule and the live acceptance record. Do not claim macOS support.

- [ ] **Step 2: Run the full suites**

Run:

```bash
dotnet test packages/host-windows/KennectComputer.Host.Tests
npm test
npm run typecheck
```

Expected: host PASS with the live-Notepad skip; MCP and shared PASS; typecheck clean.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: mark M3 and point at the Feishu acceptance runbook"
```

---

## Self-Review

**Spec coverage:** §2 goals → Tasks 1 (measurement), 3 (dry run), 4 (runbook), 5 (live), 7 (docs). §3 non-goals → no new tool or adapter anywhere. §4 constraints → Global Constraints and Tasks 3/4/5. §6 flow → Task 4's sequence and Task 3's dry run. §7 deliverables → Tasks 1, 3, 4, 5, 7. §8 acceptance → Task 5 and Task 7. §9 risks → Review Focus and Task 4's abort rules. §10 user decisions → Task 1 Step 1 and Task 5 Step 1. §11 builder questions → Task 3's harness and Task 6's contingency.

**Type consistency:** `ActionHarness`, `OwnedWindowApi`, `M3ChatTestWindow`, `M3FlowTests`, and the existing `M2InputCollection` names are used consistently. No shared schema or MCP type changes.

**Step scan:** Every code task has a failing test, a run-to-fail, a minimal implementation, a run-to-pass, and a commit. The live tasks have explicit inputs, checklists, and abort rules. No step says "handle edge cases" or "TBD".

**Review Focus:** Ambiguous titles, wrong-header Send, exact CJK draft, live-thread topology, and the live abort rule each have an owning test or runbook rule.
