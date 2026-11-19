# Kennect Computer — M3 Feishu Reply Flow Design

**Status:** Approved (user, 2026-11-19)
**Date:** 2026-11-19
**Owner:** Kenneth Igbo
**Milestone:** M3 — Feishu flow
**Spec authority:** `DESIGN.md` §16, §21 (acceptance 11), §6 (authorization)

## 1. Intent

Prove the flagship flow on real Feishu using only the generic M2 tools:
open a conversation, read recent messages, compose a reply, paste it, assert
the right conversation, send, verify it appeared, and report what went out.

M3 is an **acceptance milestone**, not a new product surface. The deliverable
is evidence that the generic tools are enough, a repeatable runbook, and only
the generic fixes the live run proves necessary.

If the live flow cannot be made reliable with generic tools, stop and escalate
to an orchestrator design (approach 2 from the conversation).

## 2. Goals

- Measure a **live Feishu thread** read-only (the M0 spike measured only the
  Assistant home; the composer and Send control in a real thread were
  explicitly deferred).
- Run the reply flow end to end in a **dedicated test conversation**.
- Leave a repeatable runbook at `scripts/acceptance-feishu.md`.
- Leave a recorded live-acceptance result.
- Fix only generic tool gaps, each with a test. No Feishu-specific product
  tool, no app adapter.

## 3. Non-goals

- A `computer_feishu_reply` tool or any app-specific tool.
- macOS (M5).
- The consequential countdown gate. It stays off; the user's instruction is
  the authorization (DESIGN §6).
- Unattended message watching or auto-replies.
- Writes to any real chat.
- Full thread history. Read the visible recent window only.

## 4. Constraints

- The live acceptance runs **only** in a dedicated test group or the user's
  own self-chat, never a real conversation. The user names the test chat and
  the exact test message before the run.
- Automated tests launch no external apps and leave no windows behind.
- Protocol v1 stays additive. No new tool unless a live gap is proven and the
  fix is generic.
- The stop hotkey (`Ctrl+Alt+Shift+Esc`) stays available through every step.
- The audit log contains every step and no message content.
- Feishu remains `preAuthorized` in config. No credentials are handled.

## 5. Approach

Approach 1 was chosen: **acceptance-first**.

- Approach 2 (Feishu orchestrator tool) is the fallback if the flow needs
  app-specific logic to be reliable.
- Approach 3 (consequential gate) was rejected for M3; it does not move the
  flow forward and conflicts with the instruction-is-authorization model.

Escalate to approach 2 only if one of these is true after the live run:

1. The flow cannot identify the Send control reliably without app-specific
   code (not merely a documented coordinate/vision step in the runbook).
2. The composer or conversation header cannot be read or asserted with the
   generic tools.
3. The flow needs more than a handful of fixed, documented selectors to stay
   reliable across a window resize or app restart.

## 6. The flow

The agent follows this sequence. Each step is a tool call; no new host method.

1. `computer_status` — host healthy and desktop unlocked.
2. `computer_windows` — find the `Feishu` window; `computer_select` it.
3. `computer_find` by the test conversation's title; click exactly one match
   in the chat list. Several matches is a stop, not a guess.
4. `computer_wait` for the right-pane document or header to show the test
   conversation's title.
5. `computer_read_text` on the thread document (TextPattern) to read the
   visible recent messages. The agent summarizes or quotes only what the user
   asked for.
6. Click the composer group, `computer_type` with `method: "paste"` and the
   instructed text, then `computer_read_text` on the composer to confirm the
   draft matches exactly.
7. Before Send, assert the conversation: the Send click carries an `expect`
   predicate for the conversation title (and, where practical, the draft
   text). A failed predicate sends nothing.
8. Identify the Send control from a fresh snapshot image (it is an unnamed
   button) and click its node or center. If the control cannot be identified
   uniquely, stop.
9. Verify: read the thread text again; the sent message must appear after
   the previous last message, and the composer must be empty.
10. Report the conversation, the sent text, and the confirmation. Check the
    audit log for the run.

The dry-run uses the same sequence against an owned, in-process chat-like
test window (conversation list, message pane, composer, unnamed Send button).
It never launches Feishu and never sends a real message.

## 7. Deliverables

- `docs/spikes/2026-11-19-windows-feishu-live-thread.md` — read-only
  measurement of a test thread: composer role/patterns, Send control,
  message text nodes, depth, and node counts.
- `scripts/acceptance-feishu.md` — the runbook, including the test-chat
  precondition, the exact sequence, the pre-send assertion, and the abort
  rules.
- A host integration test with the owned chat-like window, covering the
  sequence and the "failed pre-send expect sends nothing" case.
- `docs/acceptance/2026-11-19-feishu-reply.md` — the live run record:
  conversation, message, result, and audit confirmation (no message content
  beyond what the user supplied for the test).
- Generic fixes only if the live run proves them necessary, each with a test.
- README update: M3 status and the manual Feishu acceptance pointer.

## 8. Acceptance criteria

From DESIGN §16 and §21:

1. The agent completes the reply without the user touching mouse or keyboard
   after the run starts.
2. The sent text matches the composed text and appears in the test thread.
3. The stop hotkey aborts cleanly at a non-send step.
4. A wrong foreground or changed window identity aborts with a clear error
   and types nothing elsewhere.
5. The pre-send conversation assertion fails closed: no Send when the header
   does not match.
6. The audit log shows each step with target identity and result, and no
   message content.
7. The dry-run test passes; it launches no external app and leaves no window.
8. The host, MCP, and shared suites and typecheck stay green.

## 9. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Send is unnamed and icon-only | Fresh snapshot image; click the icon's node/center; stop if ambiguous |
| Composer differs from the Assistant home | Measure the live test thread first, before writing the runbook |
| Chat-title collisions in the list | Require exactly one match; otherwise use the chat-list row's bounds from the image |
| Virtualized message list | Read only the visible recent window; do not claim full history |
| Accidental real-chat write | Dedicated test chat only; pre-send title assertion; user names the chat and message |
| Window moves or resizes mid-flow | Re-snapshot at the compose and send checkpoints; image coordinates are invalid after a move |

## 10. Decisions left to the user

- **Test conversation:** the self-chat visible as "Igbo Kenneth Chiagozie", or
  a dedicated test group. The runbook cannot be validated without one.
- **Test message:** the exact text to send during the acceptance run. It must
  be unmistakably a test message.
- **Run timing:** the live run needs the user present for the stop hotkey and
  the explicit send instruction.

## 11. What is left to the builder

- Exact selectors and coordinates from the live-thread measurement.
- Whether the dry-run window needs a tree shape that matches Feishu closely
  enough to be meaningful, or whether the existing M2 owned-window tests plus
  the live run are sufficient.
- Any generic fix the live run exposes.
