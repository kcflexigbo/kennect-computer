# Spikes

Throwaway measurement projects. Nothing in this directory is referenced by product code.

## `windows-feishu`

Read-only probe for the Windows Feishu accessibility spike (Task 1 / M0a).

Run from the repository root:

```powershell
dotnet run --project spikes/windows-feishu
```

The probe:

- enumerates top-level windows whose process image is `Feishu.exe` (HWND, title, class, bounds, pid)
- restores and foregrounds the main Feishu window
- walks the raw UIA tree to depth 20 with a 5000-node cap, and reports two supplemental
  walks (depth 12 / cap 1500 for Task 8's defaults, and depth 60 / cap 5000 for full coverage)
- probes the chat list, message region, composer, and Send/发送 candidates, including
  supported UIA patterns and read-only text lengths
- captures the window with `PrintWindow(PW_RENDERFULLCONTENT)` to
  `docs/spikes/windows-feishu.png`

The probe never invokes a pattern, sets a value, or synthesizes input.

### Redaction

Message text, chat titles, and draft values are printed as `<redacted: N chars>`. Only a
small whitelist of structural UI labels (`Send`, `Chats`, `Feishu`, ...) is printed
verbatim. The committed transcript at `docs/spikes/windows-feishu.txt` was produced with
this default mode.

A screenshot can contain real conversation content. Ruling R6 applies: review it before
committing, and do not commit it unless the open view is clearly a dedicated test
conversation. The `docs/spikes/windows-feishu.png` from this spike run was withheld for
that reason.

### Optional environment variables

| Variable | Purpose |
|---|---|
| `KENNECT_TEST_CONVERSATION` (or first argument) | Conversation name to search for. Its value is redacted in output. |
| `KENNECT_SPIKE_SCREENSHOT` | Screenshot output path. Defaults to `docs/spikes/windows-feishu.png`. |
| `KENNECT_SPIKE_SHOW_TEXT=1` | Local-only diagnostic that previews Document text. Never use it when saving a transcript or committing output. |
