# macOS Feishu accessibility spike — 2026-11-10

**Status: PENDING MAC MEASUREMENT.** This file is a report template authored on Windows under
ruling **R1**. No macOS measurement has been made yet. Every `PENDING` placeholder below must
be replaced with exact values from the Mac run's transcript; do not infer, estimate, or copy
values from the Windows spike.

- Probe: `spikes/macos-feishu/main.swift` (read-only for app content; no AX actions invoked,
  no attributes set)
- Runbook: `spikes/macos-feishu/README.md`
- Transcript to produce: `docs/spikes/macos-feishu.txt`
- Screenshot (optional, R6): `docs/spikes/macos-feishu.png`
- Target: macOS 26.6.1 on Apple silicon, Feishu for macOS logged in on a dedicated test
  conversation

## Run context

| Item | Value |
|---|---|
| Run date/time (UTC) | PENDING |
| Mac model / chip | PENDING (`arm64` expected) |
| macOS version | PENDING (`sw_vers`) |
| Swift version | PENDING (`swift --version`) |
| Xcode path | PENDING (`xcode-select -p`) |
| Feishu owner name / pid | PENDING |
| Feishu bundle identifier | PENDING |
| Feishu `CFBundleShortVersionString` | PENDING |
| Open view / test conversation | PENDING (dedicated test group or self-chat only) |
| Window number / bounds (points) | PENDING |
| Probe exit code | PENDING (`0` expected) |
| Permissions recorded | PENDING (see below) |

### Permissions

Record exactly what macOS asked for and what was granted (brief Step 3). The probe prints
`AXIsProcessTrustedWithOptions` and `CGPreflightScreenCaptureAccess` results.

| Grant | Result | What was needed |
|---|---|---|
| Accessibility | PENDING (`granted` / `DENIED`) | PENDING (e.g. enabled Terminal/iTerm2/Claude Code or `swift-frontend`; restart required?) |
| Screen Recording | PENDING (`granted` / `DENIED`) | PENDING (only needed for the PNG) |

### Measurement definitions

- **Main walk** — application-root `AXChildren` walk, depth 20, cap 5000 (the brief's walk).
  It includes the menu bar and all app windows.
- **Deep walk** — main `AXWindow` walk, depth 60, cap 5000. This is the macOS analogue of
  Task 1's supplemental depth-60 walk and is the primary source for the message/composer/Send
  questions.
- **Interactive-role vocabulary** — `AXButton`, `AXTextField`, `AXTextArea`, `AXCheckBox`,
  `AXRadioButton`, `AXPopUpButton`, `AXComboBox`, `AXMenuItem`, `AXMenuButton`, `AXLink`,
  `AXRow`, `AXCell`, `AXTab`, `AXSlider`, `AXIncrementor`, `AXDisclosureTriangle`,
  `AXStaticText`. `AXStaticText` is included only for comparability with Task 1; it is not
  actionable by itself.
- **Coordinates** — AX positions and `CGWindowList` bounds are screen points with a top-left
  origin. On Retina, screenshot pixels are typically 2× points; the probe prints both.
- **Cap** — `capReached=true` means the 5000-node cap was hit, so counts are lower bounds.
- **Redaction** — message text, chat titles, content labels, and values are printed as
  `<redacted: N chars>` by default. The committed transcript must be the default-redacted one,
  not a `KENNECT_SPIKE_SHOW_TEXT=1` run.

## Q1 — How many AX nodes are there, and how many are interactive?

Copy the exact counts from the `Main walk`, `Deep walk`, `Q1`, and `Summary` sections.

| Walk | Total nodes | Interactive-role nodes | Non-empty string `AXValue` | Cap reached |
|---|---:|---:|---:|---|
| Main walk (app root, depth 20, cap 5000) | PENDING | PENDING | PENDING | PENDING |
| Deep walk (main window, depth 60, cap 5000) | PENDING | PENDING | PENDING | PENDING |
| Warm-up attempts before a usable tree | PENDING | — | — | — |

Role composition (paste the role-count tables; at minimum the top roles):

| Role | Main-walk count | Deep-walk count |
|---|---:|---:|
| PENDING | PENDING | PENDING |

`AXWindow` nodes in the main walk: PENDING. `AXWindows` attribute count: PENDING.

**Finding (write only after the run):** PENDING — state whether depth 20 sees the window's
content, how many nodes depth 60 adds, and whether a depth/cap change is implied for the macOS
host. Do not claim a number unless it is in the transcript.

## Q2 — Is message text exposed, and through which attribute?

Copy from the `Q2`, `Message-list element candidates`, and `Message region` sections.

| Measurement | Value |
|---|---|
| Chosen message-region container (role, bounds) | PENDING |
| Descendant nodes in the region | PENDING |
| `AXStaticText` nodes | PENDING |
| `AXStaticText` with non-empty `AXValue` | PENDING |
| `AXStaticText` with non-empty `AXTitle` | PENDING |
| `AXStaticText` with non-empty `AXDescription` | PENDING |
| `AXValue` length min / max / avg / total chars | PENDING |
| Other roles with non-empty `AXValue` | PENDING |
| Test-conversation name matches (count) | PENDING |
| Test-conversation match roles | PENDING |

**Finding:** PENDING — identify the attribute that carries message text (`AXValue` on
`AXStaticText`, `AXTitle`, `AXDescription`, an aggregate web-area value, or none). Report only
what the redacted transcript shows. If text is not exposed within the measured depth/cap, say
so and name the fallback (deeper walk, OCR, or vision).

## Q3 — Is the composer an `AXTextArea`/`AXTextField`, and does the set-value path work?

Copy from `Q3`, `Composer candidates`, `Focused UI element`, and the bottom-band settable
output. `axValueSettable=true` means `AXUIElementSetAttributeValue` on `AXValue` is supported
(the `AXSetValue` path). The probe never calls it.

| Candidate | Role / subrole | Identifier | Bounds (points) | `AXValue` attr | `AXValue` settable | `AXPress` | Focused | Enabled |
|---|---|---|---|---|---:|---:|---:|---:|---:|
| PENDING | PENDING | PENDING | PENDING | PENDING | PENDING | PENDING | PENDING | PENDING |

Bottom-band nodes with settable `AXValue`: PENDING. `AXTextArea`/`AXTextField`/`AXComboBox`
count: PENDING.

**Finding:** PENDING — state the composer's role, whether `AXValue` is readable/settable, and
whether `AXPress` exists. If `AXValue` is not settable, name the fallback (bounds-center focus
+ paste + verify). If the composer was not found in the deep walk, record `capReached` and the
depth, and say the measurement is inconclusive.

## Q4 — Is Send a button/control with `AXPress`?

Copy from `Q4`, `Send candidates`, and `Bottom-band AXButton nodes`.

| Candidate | Role | Title / description | Identifier | Bounds (points) | Actions | `AXPress` | `AXValue` attr/settable | Enabled |
|---|---|---|---|---|---:|---:|---|---:|---:|
| PENDING | PENDING | PENDING | PENDING | PENDING | PENDING | PENDING | PENDING | PENDING |

Nodes titled/described `Send`/`发送`: PENDING. Identifier contains `send`: PENDING.
Bottom-band `AXButton` count: PENDING. If there is no named Send control, identify the
icon-only candidates by position and report what the screenshot shows (if a PNG was captured
and is safe to inspect).

**Finding:** PENDING — state whether name-based targeting works, whether `AXPress` is
available, and whether geometry/vision is required. Do not identify an unnamed button as Send
without evidence from the screenshot, position, or a known identifier; label it a candidate.

## Verdict and fallback matrix

Fill every cell from the measured transcript. Keep `PENDING` wording until then.

| Element | Semantic targeting | Required fallback |
|---|---|---|
| Feishu window | PENDING | PENDING |
| Message text | PENDING | PENDING |
| Composer | PENDING | PENDING |
| Send | PENDING | PENDING |
| Cold/sparse AX tree | PENDING | PENDING |

**Overall verdict:** PENDING — state whether semantic targeting is viable on macOS and which
elements need bounds-center, clipboard paste, OCR, or vision.

## Screenshot and privacy

- Screenshot decision: PENDING (R6). Do not commit `docs/spikes/macos-feishu.png` unless the
  open view is clearly a dedicated test conversation with no third-party content.
- If withheld/deleted: record that the transcript is the committed evidence, as Task 1 did.
- If committed: record the image dimensions, scale factor, and whether the window shadow is
  included (`screencapture -l` without `-o` may include it).
- The committed transcript must be produced without `KENNECT_SPIKE_SHOW_TEXT=1`.

## Limitations

Record the actual limitations after the run. Likely items to confirm or discard:

- Feishu version and open view (dedicated test conversation?).
- Whether the main walk or deep walk hit the 5000-node cap.
- Whether the AX tree needed warm-up retries and how many.
- Whether the composer/Send area was inside the measured depth.
- Whether `screencapture -l` included the window shadow.
- Any TCC attribution issue (which app or binary received the grants).

## Mac runner checklist

```bash
# 1. Toolchain
xcode-select -p
swift --version
sw_vers
uname -m

# 2. Open the dedicated test conversation and keep Feishu visible.

# 3. Run (redacted transcript; add KENNECT_SPIKE_CAPTURE=1 for the optional PNG)
KENNECT_TEST_CONVERSATION='<test chat name>' \
swift spikes/macos-feishu/main.swift > docs/spikes/macos-feishu.txt

# 4. Replace every PENDING marker with exact transcript values.
# 5. Decide the PNG under R6.
# 6. Commit (no Claude/Anthropic attribution):
git add spikes/macos-feishu docs/spikes/2026-11-10-macos-feishu.md docs/spikes/macos-feishu.txt
git commit -m "spike: measure Feishu AX coverage on macOS"
```

If the transcript is not produced, or Accessibility/Screen Recording cannot be granted, leave
this report PENDING and report the blocker rather than filling placeholders.
