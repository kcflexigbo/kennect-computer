# Windows Feishu live-thread measurement — 2026-11-19

Read-only M3 measurement of a real Feishu chat thread. The test conversation was
the user's self-chat **"Igbo Kenneth Chiagozie"** (tag `All Staff`). The run
selected Feishu, clicked the self-chat's title in the chat list, and read the
thread, the composer, and the accessibility tree.

**No message was sent and no input was typed.** The only actions were
`computer_windows`, `computer_select`, `computer_find`, one click on the
self-chat title, one `computer_snapshot`, and two `computer_read_text` calls.

## Environment

| Item | Value |
|---|---|
| OS | Windows 11 Pro 10.0.26300 |
| Host | Kennect Computer 0.1.0, protocol 1 |
| Feishu window | `w_1801864377574359046`, pid 31244, title `Feishu` |
| Window bounds | x 2871, y 243, 1997 × 1148 |
| Snapshot | `mode: both`, `maxDepth: 60`, `maxNodes: 3000` |
| Image | scale 1.2735969387755102, `sourceBounds` = (2871, 243, 1997, 1148) |
| Tree size | `totalNodes: 523`, `truncated: false` |

The image was inspected during the run and is not committed (it shows the signed-in
account's Feishu view).

## Chat list

| Field | Value |
|---|---|
| Chat-list document | role `document`, name `messenger`, identifier `RootWebArea`, bounds (3059, 271, 448, 1106) |
| Test-chat title | role `text`, name `Igbo Kenneth Chiagozie`, bounds (3123, 1200, 147, 17) |
| Test-chat row | role `group`, name empty, bounds (3067, 1190, 432, 60) |
| Test-chat tag | role `text`, name `All Staff`, bounds (3277, 1203, 35, 12) |

### Title matching is not unique

`computer_find` with `{"text":"Igbo Kenneth Chiagozie","role":"text"}` returned
**four** matches because previews and other conversations contain the name:

| Node | Name | Bounds |
|---|---|---|
| `n5:48` | `Igbo Kenneth Chiagozie's Feishu Assistant` | (3123, 580, 259, 17) |
| `n5:104` | `Docs Assistant: @Igbo Kenneth Chiagozie关注飞书的上手指南，帮助你快速地了解和使用飞书！` | (3123, 1101, 363, 14) |
| `n5:114` | **`Igbo Kenneth Chiagozie`** | **(3123, 1200, 147, 17)** |
| `n5:118` | `Igbo Kenneth Chiagozie joined Igbo Kenneth Chiagozie. New members can view all chat history.` | (3123, 1225, 362, 14) |

**Identification rule:** choose the match whose `name` equals the test-chat title
exactly and whose bounds sit inside the chat-list row. Do not click a match whose
name only contains the title. If more than one node satisfies both conditions,
stop.

The click on `n5:114` used `method: "bounds"` (the text node is not invokable) and
opened the thread. The header then showed `Igbo Kenneth Chiagozie`, the `1`
button, and the `All Staff` tag.

## Thread

| Field | Value |
|---|---|
| Thread document | role `document`, name `messenger-chat`, identifier `RootWebArea`, bounds (3513, 271, 1341, 1106) |
| Header title | role `text`, name `Igbo Kenneth Chiagozie`, bounds (3573, 295, 193, 19) |
| Header tag | role `text`, name `All Staff`, bounds (3810, 299, 35, 12) |

### Message text

The thread contained one visible system message (the self-chat join notice), split
across four `Text` nodes:

| Node | Name | Bounds |
|---|---|---|
| `n6:165` | `Igbo Kenneth Chiagozie` | (3929, 354, 126, 14) |
| `n6:166` | `joined` | (4055, 354, 41, 14) |
| `n6:167` | `Igbo Kenneth Chiagozie` | (4095, 354, 127, 14) |
| `n6:168` | `. New members can view all chat history.` | (4221, 354, 218, 14) |

The thread document is at main-tree node `n6:136`; these text nodes appear inside
the depth-60 walk and were not truncated. A real user message is expected to
expose the same `Text`-node channel.

## Composer

| Field | Value |
|---|---|
| Composer container | role `group`, name empty, bounds (3542, 1320, 1033, 34) |
| Composer input | role `group`, name empty, identifier none, bounds (3546, 1320, 1018, 22), actions `setValue`, `scrollIntoView`, `focus`; focused |
| Placeholder | role `text`, name `Message Igbo Kenneth Chiagozie`, bounds (3546, 1322, 204, 17) |
| Hint | role `text`, name `Shift + Enter to add a new line`, bounds (4697, 1357, 137, 12) |

The composer is a `Group`, not an `Edit`, matching the M0 spike. Paste after
focusing it remains the transport.

## Send control

The Send control is unnamed. The composer row contains **eight** unnamed
`Button` nodes between x 4575 and x 4823:

| Node | Bounds | Width |
|---|---|---|
| `n6:176` | (4575, 1318, 26, 26) | 26 |
| `n6:179` | (4605, 1318, 26, 26) | 26 |
| `n6:181` | (4635, 1318, 26, 26) | 26 |
| `n6:182` | (4665, 1318, 34, 26) | 34 |
| `n6:185` | (4703, 1318, 26, 26) | 26 |
| `n6:187` | (4733, 1318, 26, 26) | 26 |
| **`n6:188`** | **(4767, 1318, 36, 26)** | **36** || `n6:189` | (4803, 1318, 20, 26) | 20 |

The screenshot shows the paper-plane icon at the far right of the composer row,
in the 36 × 26 button `n6:188` (role `button`, name empty, identifier none,
actions `press`, `scrollIntoView`, `focus`); the narrow 20 × 26 button `n6:189`
immediately to its right is the adjacent menu arrow.

**Identification rule:** Send is the unnamed 36 × 26 button at the far right of
the composer row, immediately left of the unnamed 20 × 26 menu arrow, and its
icon in the snapshot image is the paper plane. If the 36 × 26 button is absent,
another width matches it, or the image does not show the paper plane at that
position, stop.

## Text channels

| Target | Observation |
|---|---|
| Thread document `n6:136` | `read_text` length **276**. It includes the header, the system message, the composer placeholder, `Shift + Enter to add a new line`, and many object-replacement characters. It includes the latest visible message. |
| Composer input `n6:170` | `read_text` length **36**, text `Message Igbo Kenneth Chiagozie\n​\n​\n​`. An empty composer is **not** an empty string; it returns the placeholder plus zero-width spaces. |

Consequences for the runbook:

- Verify a sent message by looking for the exact sent text in the thread
  document's `read_text` (or a matching `Text` node), not by treating the whole
  read as the message list.
- Verify a draft by checking that the composer's `read_text` contains the exact
  draft. Do not assert an empty composer by `text === ""`; after Send, require
  that the sent text is no longer present and the placeholder has returned.

## Limitations

- The self-chat had no prior user message, only the join notice. A busy thread may
  virtualize older messages; read the visible recent window only.
- The measurement was taken with the window at 1997 × 1148 and 127.36% image
  scale. A resized window changes the image-space coordinates; re-snapshot after
  any move or resize.
- The image was not committed; the measurements above are the evidence.
