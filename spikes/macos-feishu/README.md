# macOS Feishu accessibility spike

Throwaway M0b measurement for the Kennect Computer macOS host. It answers the same four
questions as the Windows Task 1 spike, using the macOS Accessibility (AX) API and
`screencapture`:

1. How many AX nodes are there, and how many are interactive?
2. Is message text exposed, and through which attribute (`AXValue`, `AXTitle`,
   `AXDescription`)?
3. Is the composer an `AXTextArea`/`AXTextField`, and is its `AXValue` settable (the
   `AXSetValue` path)?
4. Is Send a button/control with `AXPress`? What is its role, name/description/identifier,
   and position?

The probe is **read-only for app content**:

- It never calls `AXUIElementPerformAction`, never calls
  `AXUIElementSetAttributeValue`, and never synthesizes input. It only reads attributes and
  queries which actions/attributes are supported.
- Its only side effects are the optional screenshot file (`KENNECT_SPIKE_CAPTURE=1`) and the
  optional `KENNECT_SPIKE_SHOW_TEXT=1` previews (unsafe; local validation only).
- It does not activate or resize Feishu.

## Status and context

This probe, this runbook, and the PENDING report template were authored on Windows under
ruling **R1** (Task 2 cannot run in the Windows worktree). The measurement itself must run on
the MacBook: macOS 26.6.1 on Apple silicon, with Xcode and Claude Code installed and Feishu
logged in. The report at `docs/spikes/2026-11-10-macos-feishu.md` stays **PENDING MAC
MEASUREMENT** until that run is complete.

## Preconditions

- macOS 26.6.1 (Apple silicon) and a Swift toolchain (`swift` on `PATH`).
- Feishu for macOS installed and logged in.
- A **dedicated test group or self-chat** open. Never point this at a real counterpart.
- The Feishu window visible and not minimized (AX reads work minimized, but the screenshot
  does not).
- The terminal/Claude Code process granted Accessibility and, if you want a screenshot,
  Screen Recording.

## Step 1 — Confirm the toolchain

Run from the repository root:

```bash
xcode-select -p
swift --version
sw_vers
uname -m
```

Expected: an Xcode path, a Swift version, macOS 26.6.1, and `arm64`. If `swift` is missing,
install the Xcode command-line tools (`xcode-select --install`) or Xcode, then re-check.

## Step 2 — Grant permissions

The probe checks both grants and prints setup guidance when either is missing.

### Accessibility (required for the AX tree)

1. Open **System Settings → Privacy & Security → Accessibility**.
2. Enable the app that is running this command. That is normally **Terminal**, **iTerm2**, or
   **Claude Code**. macOS may instead list `swift-frontend`; enable that if it appears.
3. If the app is not listed, click **+** and add it.
4. Quit and reopen that app if the grant does not take effect, then re-run the probe.

The probe calls `AXIsProcessTrustedWithOptions` with the prompt option, so macOS may show the
Accessibility prompt on the first run. Without the grant, AX calls fail with
`kAXErrorAPIDisabled` (`-25211`).

### Screen Recording (only needed for the screenshot)

1. Open **System Settings → Privacy & Security → Screen Recording**.
2. Enable the same app.
3. Quit and reopen it if the grant does not take effect.

The probe calls `CGPreflightScreenCaptureAccess` and, when missing,
`CGRequestScreenCaptureAccess`. Without this grant, `screencapture -l <windowNumber>` fails or
produces a blank/black image. When `KENNECT_SPIKE_CAPTURE` is not `1`, no screenshot is
attempted, so the grant is optional for the measurement; the probe still reports its status
and may trigger the macOS prompt at startup.

Record exactly what macOS prompted for and what you granted; the report template has a
permissions row for it (brief Step 3).

**TCC attribution note:** `swift main.swift` runs through the Swift driver. macOS may attribute
the request to the parent terminal/Claude Code or to `swift-frontend`. Grant whichever app
macOS lists. If the grant still does not apply, compile the probe to a standalone binary and
grant that binary:

```bash
swiftc spikes/macos-feishu/main.swift -o /tmp/kennect-macos-feishu
```

## Step 3 — Select the test conversation

1. Open the dedicated test group or self-chat in Feishu. Do not use a real conversation.
2. Keep the Feishu window visible and unminimized.
3. Prefer setting the exact conversation name so the probe can search for it:

   ```bash
   export KENNECT_TEST_CONVERSATION='Kennect Spike Test'
   ```

   The name is redacted in the transcript. The first CLI argument is an alternative:
   `swift spikes/macos-feishu/main.swift 'Kennect Spike Test'`.
4. If no name is supplied, the probe falls back to the Feishu window title when it is not the
   generic `Feishu`/`飞书`/`Lark`. A window title often includes a suffix; the explicit
   environment variable is more reliable.

## Step 4 — Run the probe

From the repository root:

```bash
swift spikes/macos-feishu/main.swift > docs/spikes/macos-feishu.txt
```

To watch progress while saving the same transcript:

```bash
swift spikes/macos-feishu/main.swift | tee docs/spikes/macos-feishu.txt
```

With the conversation name and the optional screenshot:

```bash
KENNECT_TEST_CONVERSATION='Kennect Spike Test' \
KENNECT_SPIKE_CAPTURE=1 \
swift spikes/macos-feishu/main.swift | tee docs/spikes/macos-feishu.txt
```

When capture is enabled, the probe runs:

```bash
/usr/sbin/screencapture -x -l <windowNumber> docs/spikes/macos-feishu.png
```

The window number is the one printed in the `CGWindowListCopyWindowInfo` section. You can also
run that command manually; the probe prints it with the observed window number when capture is
disabled.

Expected result: a transcript with the sections listed below and, if requested, a PNG. The AX
walk can take from a few seconds to a couple of minutes on a large Electron tree; the
transcript can be several thousand lines when the 5000-node cap is reached. `capReached=true`
is informational, not a failure.

### Output map

| Transcript section | Report question |
|---|---|
| `=== Permissions ===` | Environment/permissions table |
| `=== CGWindowListCopyWindowInfo: Feishu windows ===`, `Main window`, `App version` | Window identity, bundle id, version, bounds |
| `=== AX tree: main walk ... ===`, `=== Q1 ...`, `=== AX tree: deep walk ... ===` | Q1 total/interactive nodes and role counts |
| `=== Q2 ... ===`, `Message-list element candidates`, `Message region` | Q2 text exposure |
| `=== Q3 ... ===`, `Composer candidates`, `Focused UI element`, `Bottom-band ... settable` | Q3 composer role/Value/set-value |
| `=== Q4 ... ===`, `Send candidates`, `Bottom-band AXButton nodes` | Q4 Send role/name/actions/position |
| `=== Summary ... ===` | Quick counts for filling the report |

`axPress=...` reports whether the `AXPress` action is in the element's action list.
`axValueAttr=...` reports whether the `AXValue` attribute exists.
`axValueSettable=true` is the `AXSetValue` path: `AXUIElementSetAttributeValue` on `AXValue`
is supported. The probe only reads these flags; it never invokes or sets anything.

## Redaction rule

Default output redacts:

- message text and draft text: `AXValue` strings print as `<redacted: N chars>`;
- chat titles and window titles: `<redacted: N chars>` unless the title is the generic app
  name;
- content-node titles/descriptions (`AXStaticText`, `AXWebArea`, `AXGroup`, rows, cells,
  lists, documents, links, images): lengths only;
- non-whitelisted control labels: lengths only.

Only a small structural-label whitelist (`Send`, `发送`, `Feishu`, `Chats`, `Settings`, ...)
and structural identifiers print verbatim. Identifiers are capped at 80 characters.

`KENNECT_SPIKE_SHOW_TEXT=1` is an **unsafe** local-validation escape hatch that prints the
first 80 characters of `AXValue` strings. Never use it when saving or committing a transcript.
Never commit a transcript produced with it.

A screenshot can contain real conversation content. Ruling **R6** applies: do not commit
`docs/spikes/macos-feishu.png` unless the open view is clearly a dedicated test conversation
with no third-party content. Otherwise keep the PNG local, delete it, and commit only the
redacted transcript. The probe does not decide this for you.

## Environment variables

| Variable | Purpose |
|---|---|
| `KENNECT_TEST_CONVERSATION` | Exact test conversation name to search for. Its value is redacted in output. The first CLI argument is an alternative. |
| `KENNECT_SPIKE_CAPTURE=1` | Capture `docs/spikes/macos-feishu.png` with `screencapture -x -l <windowNumber>`. Off by default. |
| `KENNECT_SPIKE_SHOW_TEXT=1` | **UNSAFE** local-only preview of `AXValue` strings. Never use for a saved/committed transcript. |
| `KENNECT_SPIKE_SCREENSHOT` | Override the screenshot output path (default `docs/spikes/macos-feishu.png`). |
| `KENNECT_FEISHU_OWNER` | Override the `CGWindowListCopyWindowInfo` owner name filter. Default `Feishu`; the probe also falls back to owners containing Feishu/Lark/飞书. |

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | AX measurement completed (screenshot may have been skipped or completed) |
| 2 | No Feishu window found (or no usable window after filtering) |
| 3 | Accessibility is not granted; the AX walk was skipped |
| 4 | A screenshot was requested and the capture failed |

Exit code 2 prints the owner names it saw. Use it to set `KENNECT_FEISHU_OWNER`.

## Troubleshooting

- **`kAXErrorAPIDisabled` / Accessibility DENIED** — grant Accessibility as above, restart the
  terminal/Claude Code, and re-run. The probe exits 3.
- **Empty or sparse AX tree** — Electron enables accessibility on demand. The probe retries up
  to three times with 1.5 s waits when it does not see named static text and a large container.
  Make sure Feishu is running with a window open; relaunch Feishu or Run again.
- **No Feishu window** — bring Feishu out of the Dock/hidden state. If the owner name is
  localized, set `KENNECT_FEISHU_OWNER`. The probe prints the layer-0 owner names it saw.
- **Window title `<unavailable>`** — `kCGWindowName` is withheld without Screen Recording.
  Grant it and re-run if you need titles or a screenshot.
- **Blank or black screenshot** — Screen Recording is missing or the grant has not applied.
  `CGPreflightScreenCaptureAccess()` must be true. Grant it, restart the app, and re-run.
- **Grant does not take effect for `swift`** — see the TCC attribution note above; grant the
  listed app or compile a binary with `swiftc` and grant that.
- **Slow walk / `capReached=true`** — expected on a busy Electron tree. The 5000-node cap is a
  safety limit; record it in the report.
- **Coordinates** — AX positions and `CGWindowList` bounds are screen **points** with a
  top-left origin. On a Retina display the screenshot is usually **2× pixels**; the probe
  prints the image pixel size and the scale factor. `screencapture -l` may include the window
  shadow; for exact node-to-pixel mapping, capture manually with `-o`
  (`screencapture -x -o -l <windowNumber> <path>`). The probe follows the brief's `-x -l`.

## After the run

1. Fill `docs/spikes/2026-11-10-macos-feishu.md` with the exact counts from
   `docs/spikes/macos-feishu.txt`. Replace every `PENDING` marker; do not invent values.
2. Decide the screenshot under R6. Keep it out of the commit unless it is clearly a dedicated
   test conversation.
3. Commit (no Claude/Anthropic attribution in the message):

   ```bash
   git add spikes/macos-feishu docs/spikes/2026-11-10-macos-feishu.md docs/spikes/macos-feishu.txt
   # add docs/spikes/macos-feishu.png only if R6 allows it
   git commit -m "spike: measure Feishu AX coverage on macOS"
   ```

The probe is throwaway and is never referenced by product code.
