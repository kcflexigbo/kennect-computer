# Feishu reply acceptance runbook

End-to-end acceptance for M3 (DESIGN §16, §21). The run sends **one** test
message in the dedicated self-chat and verifies it appears. It uses only the
generic tools.

- Measurement: `docs/spikes/2026-11-19-windows-feishu-live-thread.md`
- Design: `docs/superpowers/specs/2026-11-19-m3-feishu-reply-flow-design.md`
- Record the run in `docs/acceptance/2026-11-19-feishu-reply.md`

## 1. Preconditions

- **Test chat:** the self-chat **"Igbo Kenneth Chiagozie"**. Never point this
  run at a real conversation.
- **Test message:** the exact text `<TEST_MESSAGE>` supplied by the user for
  this run. It must be unmistakably a test message.
- Feishu is running and logged in.
- The Kennect host is running from the M3 branch; the desktop is unlocked.
- Feishu is authorized (`preAuthorized` or `allow-all`); the stop hotkey is
  `Ctrl+Alt+Shift+Esc`.
- The user is present for the stop-hotkey check and has explicitly asked for
  this send.

Only one message may be sent. No attachments, mentions, or edits.

## 2. Safety rules

1. Never run against a real chat. The self-chat is the only allowed target.
2. Do **not** press `Enter` while the composer is focused — Feishu's default
   Enter may send. Send only through the Send control.
3. Stop before any input if the window, chat title, header, or Send control is
   ambiguous.
4. Stop if `computer_status` reports `locked: true`.
5. One message only. If the draft is wrong, clear it with `Ctrl+A`, `Delete`
   and start over; never "send and correct".
6. Never type credentials or read password fields.

## 3. Runtime node references

Node IDs are ephemeral. Resolve each of these from the latest snapshot:

| Name | How to resolve |
|---|---|
| `CHAT_TITLE_NODE` | The `text` node whose name equals `Igbo Kenneth Chiagozie` exactly and whose bounds are inside the chat-list row (measured at 3123, 1200, 147, 17) |
| `THREAD_DOCUMENT_NODE` | The `document` node named `messenger-chat` with identifier `RootWebArea` (measured at 3513, 271, 1341, 1106) |
| `HEADER_NODE` | The `text` node named `Igbo Kenneth Chiagozie` in the thread header, measured at y ≈ 295 inside the thread pane (x ≥ 3513) |
| `COMPOSER_NODE` | The `group` at the composer bounds with actions `setValue`, `scrollIntoView`, `focus` (measured at 3546, 1320, 1018, 22) |
| `SEND_NODE` | The unnamed 36 × 26 `button` at the far right of the composer row, immediately left of the unnamed 20 × 26 menu arrow (measured at 4767, 1318, 36, 26) |

Measured values are from the 2026-11-19 run at window 1997 × 1148. Roles and
names are stable; re-measure bounds after a resize.

## 4. Sequence

### Preflight

1. `computer_status` with `{}`.
   Require `ok: true` and `locked: false`.

2. `computer_windows` with `{}`.
   Require exactly one window with `processName: "Feishu"`. Record its
   `windowId`. If there are none or several, stop.

3. `computer_select` with:

   ```json
   { "windowId": "<the Feishu windowId>" }
   ```

   Require the returned window title is `Feishu` and process is `Feishu`.

### Open the test chat

4. `computer_find` with:

   ```json
   { "text": "Igbo Kenneth Chiagozie", "role": "text" }
   ```

   Expect four matches: the title, one assistant title, one docs preview, and
   one join-notice preview. Choose the node whose `name` is exactly
   `Igbo Kenneth Chiagozie` and whose bounds are inside the chat-list row.
   If none or more than one satisfies both conditions, stop.

5. `computer_click` with:

   ```json
   { "nodeId": "<CHAT_TITLE_NODE>" }
   ```

   Require `ok: true`. Then take a fresh snapshot (step 7) and require the
   thread header is exactly `Igbo Kenneth Chiagozie`.

6. `computer_wait` with:

   ```json
   {
     "condition": { "kind": "node", "role": "document", "name": "messenger-chat" },
     "timeoutMs": 10000
   }
   ```

   This confirms the thread pane replaced the previous tab. A timeout means
   the chat did not open; stop.

### Read the thread

7. `computer_snapshot` with:

   ```json
   {
     "windowId": "<the Feishu windowId>",
     "mode": "both",
     "image": true,
     "maxDepth": 60,
     "maxNodes": 3000
   }
   ```

   Resolve `CHAT_TITLE_NODE`, `THREAD_DOCUMENT_NODE`, `HEADER_NODE`,
   `COMPOSER_NODE`, and `SEND_NODE` from this snapshot. Require the header's
   name is exactly `Igbo Kenneth Chiagozie`. If not, stop.

8. `computer_read_text` with:

   ```json
   { "nodeId": "<THREAD_DOCUMENT_NODE>", "maxLength": 50000 }
   ```

   Record the current last visible message. Note that the text includes the
   header, the composer placeholder, and object-replacement characters; it is
   not a pure message list.

### Stop-hotkey check

9. The user presses `Ctrl+Alt+Shift+Esc` during a non-send step. Use a wait so
   there is something to abort:

   ```json
   {
     "condition": { "kind": "text", "contains": "kennect-stop-check" },
     "timeoutMs": 60000
   }
   ```

   Require `ACTION_TIMEOUT` with “The operation was stopped.” and a host that
   still answers `computer_status`. Then repeat step 7's snapshot request
   before continuing; resolve every node reference from that new snapshot.

### Compose

10. `computer_type` with:

    ```json
    {
      "nodeId": "<COMPOSER_NODE>",
      "method": "paste",
      "text": "<TEST_MESSAGE>"
    }
    ```

    Require `ok: true` and `method: "paste"`. Do not press Enter.

11. `computer_read_text` with:

    ```json
    { "nodeId": "<COMPOSER_NODE>", "maxLength": 50000 }
    ```

    Require the returned text contains `<TEST_MESSAGE>` exactly. If it does
    not, clear the draft with `Ctrl+A`, `Delete`, and stop.

### Send

12. Take a fresh snapshot immediately before Send, using the step 7 request
    body:

    ```json
    {
      "windowId": "<the Feishu windowId>",
      "mode": "both",
      "image": true,
      "maxDepth": 60,
      "maxNodes": 3000
    }
    ```

    Re-resolve `HEADER_NODE` by its measured bounds (role `text`, name exactly
    `Igbo Kenneth Chiagozie`, y ≈ 295 inside the thread pane, x ≥ 3513) and
    call `computer_read_text` on it. Require the header text is exactly
    `Igbo Kenneth Chiagozie`. Re-resolve `THREAD_DOCUMENT_NODE`,
    `COMPOSER_NODE`, and `SEND_NODE` from this snapshot. If the header does not
    match, stop and clear the draft; do not send.

13. Confirm exactly one `SEND_NODE` candidate: an unnamed 36 × 26 button at the
    far right of the composer row, immediately left of the unnamed 20 × 26 menu
    arrow, with the paper-plane icon in the fresh snapshot image. If the image
    does not show the paper plane at that position, or more than one candidate
    matches, stop.

14. `computer_click` with:

    ```json
    {
      "nodeId": "<SEND_NODE>",
      "expect": [
        { "kind": "node", "role": "text", "name": "Igbo Kenneth Chiagozie" }
      ]
    }
    ```

    The expectation must pass; a failure returns `EXPECTATION_FAILED` and
    sends nothing. The `kind: "node"` predicate is a window-wide existence
    check, not a header-scoped assertion: live Feishu exposes the same title in
    the chat list, so step 12's fresh header read is the binding check. A
    `kind: "text"` predicate requires `contains`; the shared schema rejects one
    without it.

### Verify

15. Take a fresh snapshot and `computer_read_text` on
    `<THREAD_DOCUMENT_NODE>`. Require the exact `<TEST_MESSAGE>` appears and is
    the newest visible message.

16. `computer_read_text` on `<COMPOSER_NODE>`. Require the placeholder
    `Message Igbo Kenneth Chiagozie` is present and `<TEST_MESSAGE>` is absent.

17. Check the audit log at
    `%LOCALAPPDATA%\KennectComputer\audit\YYYY-MM-DD.jsonl` (or
    `<KENNECT_COMPUTER_SESSION_DIR>\audit` when set). Require entries for this
    run’s tools; a type entry carries `textLength` and `textSha256`, never the
    text or message content.

## 5. Send identification rule

At the measured size the composer row has eight unnamed buttons between
x ≈ 4575 and x ≈ 4823. Send is the **36 × 26** one at x ≈ 4767, immediately
left of the **20 × 26** menu arrow at x ≈ 4803; its image-space center is
approximately (1503, 854) at image scale 1.2736. The narrower 20 × 26 button is
the menu, not Send.

At another window size, use the same relative rule: the unnamed button at the
far right of the composer row, immediately left of the narrow menu arrow, whose
icon is the paper plane. Require exactly one candidate.

## 6. Abort rules

Stop with no further input when any of these occurs:

- no Feishu window, or more than one;
- the `computer_find` title match is ambiguous;
- the thread header is not exactly `Igbo Kenneth Chiagozie`;
- the fresh pre-Send header read (step 12) is not exactly `Igbo Kenneth Chiagozie`;
- the thread document or composer cannot be resolved;
- the draft read-back does not contain `<TEST_MESSAGE>`;
- the Send control is missing or ambiguous, or the image does not show the
  paper plane;
- `FOCUS_REFUSED`, `WINDOW_GONE`, or `STALE_SNAPSHOT`;
- the user says stop.

If a draft is present when aborting, clear it with `Ctrl+A`, `Delete` and
verify the composer is back to the placeholder. If clearing fails, close
Feishu without sending and report.

## 7. Evidence

Record in `docs/acceptance/2026-11-19-feishu-reply.md`:

- date/time, host version, Feishu window ID and PID;
- test chat and the exact `<TEST_MESSAGE>`;
- each step’s result, including the stop-hotkey result;
- the verification read-back (the message appears, composer cleared);
- the audit file path and the entries that cover the run.

Do not record any other conversation content.
