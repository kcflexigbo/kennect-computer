# Windows Feishu accessibility spike — 2026-11-10

Throwaway M0a measurement (Task 1). The probe is read-only **for app content**: it
enumerates windows, reads the UIA tree, and captures one screenshot. It never invokes a
pattern, sets a value, or synthesizes input. It does perform the brief-mandated
`ShowWindow(SW_RESTORE)`/`SetForegroundWindow`; it captures the original
`WINDOWPLACEMENT` first and restores it after the screenshot (ruling R12).

- Transcript: `docs/spikes/windows-feishu.txt`
- Screenshot: captured to `docs/spikes/windows-feishu.png` but **not committed** (privacy;
  see [Screenshot and privacy](#screenshot-and-privacy)).
- Probe: `spikes/windows-feishu/` (`dotnet run --project spikes/windows-feishu`).

## Run context and controller waivers (R10, R12)

The brief's preconditions are Feishu **8.5.6** logged in on a **dedicated test
conversation**. This run used Feishu **8.4.0.18** on the **Assistant home**, not a live
test thread. Ruling **R10** waives both preconditions for this spike.

- Every finding below is labeled for the Assistant-home/8.4.0.18 surface. Q2 has no
  live-thread per-message counts; Q3/Q4 are Assistant-home-only. The live-thread
  measurement is **deferred to the Task 8 kickoff**, where a test group or self-chat is
  opened before the snapshot work starts. No claim in this report is made for a live
  Feishu 8.5.6 test conversation.
- The only state changes are the mandated window restore/activation. This run's original
  placement was minimized (`showCmd=2`, normal rect 1317x750 at (2853,268)); the probe
  restored the window for measurement and restored the original placement after capture
  (`SetWindowPlacement -> True`). Ruling **R12** accepts this transient geometry change.

## Environment

| Item | Value |
|---|---|
| OS | Windows 11 Pro 10.0.26300 |
| .NET SDK | 10.0.102 |
| Running Feishu image | `%LOCALAPPDATA%\Feishu\app.20261109-125048-625Z\Feishu.exe` |
| Running Feishu FileVersion | **8.4.0.18** (the brief says 8.5.6; the running package reports 8.4.0.18) |
| Main window | `Chrome_WidgetWin_1`, pid 9180, title `Feishu`; minimized at enumeration (`showCmd=2`), measured 1317x750 at (2853,268) after `SW_RESTORE` |
| Renderers | two `Chrome_RenderWidgetHostHWND` children: chat list 448x708, right pane 661x708 |
| Open view during the run | Feishu Assistant home, not a third-party chat thread |

`PrintWindow(PW_RENDERFULLCONTENT)` returned True; the image is 1317x750 and not mostly
black. Because the probe is per-monitor DPI aware, UIA bounds and screenshot pixels are 1:1
for this window.

## Q1 — How many UIA nodes are there, and how many are interactive?

Node counts from three walks of the same warm tree (raw view; no truncation in any walk):

| Walk | Total nodes | Interactive (Task 8 roles) | Enabled | On-screen |
|---|---:|---:|---:|---:|
| Depth 12, cap 1500 (Task 8 defaults) | **119** | **58** | 54 | 58 |
| Depth 20, cap 5000 (spike brief) | **312** | **91** | 87 | 86 |
| Depth 60, cap 5000 (full, supplemental) | **647** | **189** | 185 | 151 |

Depth-20 composition: Group 169, Button 57, Pane 40, Text 17, TabItem 15, MenuBar 3,
TitleBar 3, Document 2, Image 2, ScrollBar 1, Separator 1, Tab 1, Window 1.

Only two `Document` nodes exist, both Chromium web-area roots (`AutomationId=RootWebArea`):
the 448x708 chat list and the 661x708 right pane. The right-pane Document is at **main-tree
depth 10**. At depth 20 it has 23 descendants and **zero** `Text` children. The deep walk
under that Document finds Text nodes at **Document-relative depths 14-35**; because the
Document is at main-tree depth 10, those Text nodes are at **main-tree depths 24-45**.

**Finding:** Task 8's default `MaxDepth = 12` is too shallow for Feishu. It sees 119 of 647
nodes (18%) and 58 of 189 interactive nodes (31%), and it misses every text node in the
web areas. Raise `MaxDepth` to **at least 45** (60 is the measured full walk) or special-case
a deep walk under web-area `Document` nodes. A depth of 20 or 40 still misses message text.
`MaxNodes = 1500` is sufficient for this view (647 nodes) but keep the 5000 safety cap for
busy threads.

## Q2 — Is message text exposed, and through which property?

Yes, through the **`Name` property of `ControlType=Text` nodes**. Text nodes support
`Text`, `TextChild`, and `TextEdit` patterns but **not** `ValuePattern`. UIA `TextPattern`
is available on each web-area `Document` as an aggregate read-only text channel.

| Web area | Text nodes (deep) | Name length min/max | Total chars | Document TextPattern | Document ValuePattern |
|---|---:|---:|---:|---|---|
| Chat list (448x708) | **51** | 2 / 100 | 883 | 1242 chars, 88 lines | 119 chars, read-only |
| Right pane (661x708) | **45** | 1 / 49 | 571 | 787 chars, 81 lines | 124 chars, read-only |

The chat-list text includes conversation titles and message previews. The right-pane text
is the open assistant page's content. The `Document.ValuePattern` is a short read-only
summary (119-124 chars), **not** the full text; use `Text.Name` for individual text and
`Document.TextPattern` for the aggregate.

**Run context (R10):** these Q2 counts are from Feishu **8.4.0.18**, on the **Assistant
home plus chat list**, not a live test thread. Per-message node counts in a live thread are
not measured; the chat-list previews and assistant page demonstrate the same Chromium text
mechanism. The live-thread measurement is deferred to the Task 8 kickoff.

## Q3 — Is the composer an Edit/Document, and does ValuePattern work?

No. The composer is a **`Group`**, not an `Edit` or `Document`:

| Node | ControlType | Bounds | Patterns | ValuePattern | TextPattern |
|---|---|---|---|---:|---|
| Composer input | **Group** (#0202) | (3528,947,338,22) | LegacyIAccessible, ScrollItem, Text, TextChild, **TextEdit** | **no** | yes, `textLen=108`, 7 lines |
| Chat-list web area | Document | (3041,296,448,708) | ... Text, TextEdit, **Value** | yes, read-only | yes |
| Right web area | Document | (3495,296,661,708) | ... Text, TextEdit, **Value** | yes, read-only | yes |

The only `Edit`/`Document` nodes are the two web-area Documents, not the input.

**Run context (R10):** this is the Assistant-home composer on Feishu **8.4.0.18**; the
chat-thread composer was not open and is not verified. The live-thread check is deferred to
the Task 8 kickoff.

**Verdict:** `ValuePattern` does not work on the composer. Use bounds-center focus, then
clipboard paste (`Ctrl+V`) and verify with a fresh snapshot. `TextPattern` can read the
composer's text but cannot write it. This matches the design's paste-by-default transport.

## Q4 — Is Send a Button with InvokePattern?

There is **no node named `Send` or `发送`** (0 matches) and **no `AutomationId` containing
`send`** (0 matches). The visible arrow (paper-plane) send control is an **unnamed Button**
with `InvokePattern`:

| Candidate | ControlType | Name | Bounds | Patterns | Enabled |
|---|---|---|---|---:|---:|
| Send arrow (paper-plane, identified by position/icon) | Button | empty | (4069,945,36,26) | Invoke, LegacyIAccessible, ScrollItem, TextChild | true |
| Adjacent menu arrow | Button | empty | (4105,945,20,26) | Invoke, LegacyIAccessible, ScrollItem, TextChild | true |

The composer row contains 9 unnamed Buttons with `InvokePattern`. Name-based targeting
fails; geometry plus the screenshot (icon vision) is required. `InvokePattern` is available,
so `Invoke()` is viable once the correct button is identified; a bounds-center click is an
equivalent fallback.

**Run context (R10):** this is the Assistant-home composer row on Feishu **8.4.0.18**; the
chat-thread Send button was not open and is not verified. The live-thread check is deferred
to the Task 8 kickoff.

## Verdict and fallback matrix

Semantic targeting is viable for the window, web-area documents, text extraction, chat-list
entries, and locating the composer. It is **not** viable for the composer's value or the
Send button's name. These results are from the Assistant-home/8.4.0.18 surface (R10); the
live-thread check is deferred to the Task 8 kickoff.

| Element | Semantic targeting | Required fallback |
|---|---|---|
| Feishu window | Yes: process `Feishu.exe`, class `Chrome_WidgetWin_1`, title `Feishu`, even when minimized | — |
| Chat list / assistant text | Yes: `Text.Name` + `Document.TextPattern` | Deep walk to main-tree depth 45+; default 12 misses it |
| Composer | Locate as Group with `TextEdit`; no ValuePattern | Bounds-center focus + clipboard paste + verify |
| Send arrow | No name; unnamed Button with Invoke | Screenshot + bounds / icon vision, then Invoke or click |
| Cold a11y tree | First query can be sparse (123 vs 312 warm nodes) | Retry with a short wait; OCR/vision only as last resort |

## Screenshots and pairing with UIA

More screenshots can direct the flow, and they pair cleanly with the tree:

- The capture is **1317x750**, exactly the UIA window bounds, because the probe is
  per-monitor DPI aware. A node's `Bounds` is a crop rectangle in the screenshot (translate
  by the window origin).
- Use the image for what UIA omits: icon-only controls such as the arrow Send, the composer
  toolbar, and visual verification after each action.
- Use before/after screenshots at decision points: select conversation, paste, send. Compare
  the after-snapshot to confirm the expected change (message appears, composer clears) and
  to catch a mis-targeted click.
- UIA remains the better source for text and roles; the image is the better source for icon
  semantics and layout. `SnapshotMode.Both` is the right default for Feishu. A vision-only
  flow would work but loses precise text and is slower to verify on long threads.
- OCR is not required for Feishu message text while the tree is warm; keep it as a fallback
  for a cold/disabled accessibility tree.

## Task 8 vocabulary recommendations

1. Keep `Text`; it is the primary message-text channel. Its main-tree depths are **24-45**
   (Document-relative 14-35 under the right-pane Document at main-tree depth 10).
2. Treat `Group` as interactive when it supports `Invoke`, `TextEdit`, or `SelectionItem`.
   The composer is a Group, and 177 of the 255 deep right-pane nodes are Groups.
3. Do not drop unnamed `Button` nodes; they include the Send arrow and the composer toolbar.
4. Raise `MaxDepth` to **at least 45** (60 is the measured full walk) or walk web-area
   `Document` subtrees specially. `MaxNodes = 1500` is sufficient here; keep 5000 for busy
   threads.
5. Warm the tree: the first UIA query can return a cold, sparse tree. Retry after ~1.5 s
   when few named Text nodes or sized Documents are present.
6. Count interactive nodes by pattern support as well as ControlType; the raw role count
   understates actionable Groups.

## Screenshot and privacy

The screenshot was captured successfully (`PrintWindow(PW_RENDERFULLCONTENT)=True`,
1317x750, `mostlyBlack=False`). It is **not committed** because the open view was the
signed-in user's Feishu Assistant home showing their name and personal work content, not a
clearly dedicated test conversation (ruling R6). The file was deleted from the working tree;
the committed transcript is the evidence. `docs/spikes/windows-feishu.png` is intentionally
absent from this commit.

## Limitations

- No dedicated test conversation was open, and the probe does not open one because it is
  read-only for app content (it only performs the mandated window restore/activation).
  Re-run at Task 8 kickoff with the test group or self-chat open to measure a live thread's
  per-message nodes and chat composer (R10).
- The running build reports FileVersion 8.4.0.18, not 8.5.6. The installed launcher path may
  differ from the running versioned app directory; verify on the target build.
- `ShowWindow(SW_RESTORE)` changes window geometry for the measurement. Across runs it
  un-maximized a maximized window (2576x1408 -> 1317x750) and restored a minimized window
  (this run: original `showCmd=2`, normal rect 1317x750). The probe now captures the original
  `WINDOWPLACEMENT` and restores it after capture (`SetWindowPlacement -> True`, R12).
  The transient change is accepted by R12; it is not an app-content mutation.
- `SetForegroundWindow` returned False on the final run (True on earlier runs). UIA reads
  still worked. The product should not assume activation succeeded before typing.
- The assistant-home composer may differ from a chat-thread composer; the Group/TextEdit/no-
  ValuePattern result should be confirmed in a live chat thread at the Task 8 kickoff.
