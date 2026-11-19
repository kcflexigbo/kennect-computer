# Feishu reply acceptance record — 2026-11-19

**Result: PASS.** One test message was sent in the dedicated self-chat and
verified in the thread; the composer cleared; the stop hotkey aborted a wait;
the audit log contains the run without message content.

- Runbook: `scripts/acceptance-feishu.md`
- Measurement: `docs/spikes/2026-11-19-windows-feishu-live-thread.md`
- Design: `docs/superpowers/specs/2026-11-19-m3-feishu-reply-flow-design.md`

## Environment

| Item | Value |
|---|---|
| Date/time | 2026-11-19, 12:02 PM local (04:02 UTC) |
| Host | Kennect Computer 0.1.0, protocol 1 |
| Feishu | window `w_1801864377574359046`, pid 31244, 1997 × 1148 |
| Test chat | self-chat **Igbo Kenneth Chiagozie** (`All Staff`) |
| Test message | `Kennect M3 acceptance test — please ignore` |

## Steps

| Step | Result |
|---|---|
| `computer_status` | `ok: true`, `locked: false` |
| `computer_windows` | exactly one Feishu window |
| `computer_select` | ok; Feishu focused |
| `computer_find` title | 8 text matches because the name repeats in headers, previews, messages, and the placeholder; the exact title in the chat-list row was `n8:114` at (3123, 1200, 147, 17) |
| click title | ok, `method: bounds` |
| `computer_wait` for `messenger-chat` | matched in 579 ms |
| `computer_snapshot` | 523 nodes, `truncated: false`, image scale 1.2736, source (2871, 243, 1997, 1148) |
| read thread before send | length 276; latest visible message was the join notice |
| stop hotkey | **PASS** on retry: a 30 s wait returned `ACTION_TIMEOUT` with “The operation was stopped.” after 6.6 s; the host still answered `computer_status` |
| `computer_type` | ok, `method: paste`, `needsObservation: true` |
| read composer | length 47; contained the exact test message |
| pre-send expectation | the header was verified from the step 7 snapshot before composing. The Send click used the window-wide `kind: node`, `role: text`, `name: Igbo Kenneth Chiagozie` predicate and reported `method: pattern`. The header was **not** re-read immediately before Send in this run; the runbook now requires that binding check (step 12). |
| send | message bubble appeared at 12:02 PM as node `n11:175`, group identifier `7709728191870422264` |
| read thread after send | length 359; contained `Kennect M3 acceptance test — please ignore` after 12:02 PM |
| read composer after send | length 36; placeholder `Message Igbo Kenneth Chiagozie` returned, test message absent |

## Audit

File: `C:\Users\Kenneth Igbo\AppData\Local\KennectComputer\audit\2026-11-19.jsonl`

The run’s entries are present from `computer_status` through the final
`computer_read_text`. The type entry excerpt is:

```json
{"tool":"computer_type","result":"ok","windowId":"w_1801864377574359046","process":"Feishu","nodeId":"n10:170","textLength":44,"textSha256":"b78a0d75cf66284557b1e3c5397222a151e9e9332d097e02c1e0ced12088da84"}
```

`textLength` 44 is the UTF-8 byte length; the text itself is not recorded.
The full record also carries `timestamp`, `sessionId`, and `durationMs`; they
are omitted here for brevity. A search of the audit file for
`Kennect M3 acceptance test` returned 0 matches.

## Deviations

1. **Stop-hotkey first attempt.** A 60 s wait expired normally because the
   hotkey was not pressed. The retry passed. No input or message was affected.
2. **Invalid expectation shape.** The first pre-send request used
   `{"kind":"text","role":"text","name":"..."}`, which the shared schema
   rejected because a `text` predicate also requires `contains`. The client
   returned `-32602` before any host input. The corrected `kind: "node"`
   predicate passed.

Neither deviation changed the result. The message was sent once.

## Pre-send safety limitation

The generic `expect` predicate is window-wide: it passes when any node in the
window matches the selector, and live Feishu exposes the conversation title in
the chat list as well as in the thread header. In this recorded run the header
was verified from the step 7 snapshot before composing, and the chat did not
change during the run; the Send predicate was a coarse presence check, not a
header-scoped assertion.

The runbook now adds a binding check: a fresh snapshot immediately before Send,
an exact `computer_read_text` of the header node resolved by its measured
bounds, and a stop if the header differs (step 12). Future acceptance runs must
perform it. No Feishu-specific tool was added.

## Evidence

The post-send snapshot image was reviewed: it shows the sent bubble and the
empty composer. It is not committed because it contains the signed-in account’s
Feishu view. The node identities and text lengths above are the recorded
evidence.
