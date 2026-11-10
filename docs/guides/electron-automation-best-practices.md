# Electron Apps That Automation Can Actually Drive

**Status:** Reference guide
**Date:** 2026-11-10
**Provenance:** Derived from the Kennect Computer Windows Feishu spike (`docs/spikes/2026-11-10-windows-feishu.md`), cross-checked against Electron's `app` API docs. It started as advice from another agent; this version keeps what held up and corrects what did not.
**Audience:** Anyone building an Electron app that screen readers, UIA/AX clients, or local agents should control.

## The short version

Build for two first-class users: a screen-reader user and an operating-system accessibility client. Then add an opt-in, first-party MCP server for precision. The accessibility tree is the universal contract that every client can use; the MCP is the reliable control surface for agents that opt in.

---

## 1. Make the accessibility tree the contract

### Semantics and names

- Use real semantic elements: `<button>`, `<input>`, `<textarea>`, `<a>`, `<ul>/<li>`. Avoid `div` + click handlers and custom widgets without roles.
- Give every interactive node a stable, unique accessible name. Icon-only controls need `aria-label` (and usually `title`). Mark the inner SVG `aria-hidden="true"`.
  - Feishu's Send arrow was an unnamed `Button` with `InvokePattern`; the name was empty, so automation had to identify it by geometry or vision. `<button aria-label="Send">` would have fixed it.
- Put stable DOM `id`s on key controls. Chromium often exposes them as UIA `AutomationId`, but verify with Inspect. Avoid generated IDs and duplicate accessible names in one view.
- Expose states explicitly: `aria-selected`, `aria-expanded`, `aria-checked`, `aria-disabled`, plus native `disabled`. A styled `div` may expose none of these.

### Editable fields

- Use native `<input>`/`<textarea>` where possible. They usually map to UIA `Edit` with a settable `ValuePattern`, and to settable AX text fields.
- Rich text is the exception. If you must use it, use `contenteditable role="textbox" aria-multiline="true" aria-label="..."` and verify the resulting role and patterns. Expect automation to focus the control and paste rather than call `SetValue`.
  - Feishu's composer was a `Group` with `TextEdit`/`Text` patterns and **no `ValuePattern`**. Focus + clipboard paste was the only reliable path.
- Verify per Electron/Chromium version: a `<textarea>` can surface as a `Document` whose `ValuePattern` is read-only, not as an `Edit`.

### Text and message content

- Expose message text as real DOM text nodes. Chromium maps them to `ControlType=Text` with the text in `Name`.
- Do not put content only in canvas, images, CSS generated content, or a Document-level value. In the Feishu spike, the Document value was truncated to 119–124 characters; individual `Text` nodes carried the real content.
- Provide alt text for images and attachments, and an accessible DOM/ARIA mirror for canvas/WebGL/whiteboard surfaces.

### Lists, selection, and state

- Use `role="list"` + `listitem` for message lists, and `role="listbox"` + `option` (or rows with `aria-selected`) for conversation lists.
- Prefer `aria-selected` for selection. `aria-current` does not reliably map to `SelectionItemPattern`; verify the pattern your client actually sees.
- `role="log"` is a live region, not a list. It can be useful for announcements, but it does not by itself give you `ListItem`/`SelectionItem` semantics.
- Expose unread/active state as accessible child text or properties, not only as color, badges, or icons.

### Depth, virtualization, and layout

- Keep the DOM shallow, but do not confuse DOM depth with UIA depth. Chromium adds `Window → Pane → Document → Group` layers between your DOM and the UIA node. A key control at DOM depth 8 can be 20+ levels deep in UIA. Aim for a shallow DOM, then measure the actual tree.
- Feishu's web content appeared at UIA depths 14–35. A depth-12 walk saw 119 nodes / 58 interactive; depth 60 saw 647 / 189 and every message `Text` node. Task 8 of Kennect Computer must walk Document subtrees deeper or retry at a higher depth.
- Virtualization is a common automation killer. Keep visible and focused items mounted, expose item counts and scroll patterns, and do not unmount a focused node. Keeping every offscreen node mounted is not a reasonable trade; expose counts and virtualization-aware scrolling instead.
- Be per-monitor DPI aware. Avoid custom scaling that breaks bounds; if you use `webContents.setZoomFactor`, document that coordinates and UIA bounds change with zoom.
- The spike measured a 1:1 mapping between UIA bounds and the `PrintWindow` capture at the current DPI. Test at 100%, 150%, and 200%.

### Windows and webviews

- Give the main window a stable title. Do not put unread counts or transient state in the title; expose that as accessible child text.
- Name webviews/documents, and keep helper windows hidden and named. Feishu exposed 12 top-level windows, mostly hidden; one clearly named main window is much easier to target.

---

## 2. Electron-specific enablement

- `app.setAccessibilitySupportEnabled(enabled)` exists on **macOS and Windows**. It manually enables Chromium accessibility tree rendering, is **disabled by default**, must be called after the `ready` event, and the Electron docs warn it **can significantly affect performance**.
  - Use it for an explicit accessibility/automation mode, not as a global default. Expose the switch in settings, or enable it in test/automation builds.
- `--force-renderer-accessibility` is a Chromium switch you can pass via `app.commandLine.appendSwitch('force-renderer-accessibility')` for test builds. Verify its behavior on your Electron version; it is not a substitute for a properly named tree.
- Chromium builds accessibility on demand and can be cold. The Feishu spike saw 123 nodes on the first query and 312 after warming. Clients should retry; apps should avoid depending on a screen reader being present.
- An explicit automation mode should: force the tree on, disable animations and virtualization that unmount nodes, keep layout stable, and expose stable test IDs.

---

## 3. Ship a first-party MCP server

Apps that expect agent control should ship a local MCP server. OS accessibility is the universal compatibility layer; an MCP is the reliable control layer.

- **Transport:** prefer stdio MCP for local agents. If a UI integration needs a socket, use loopback HTTP/WebSocket with a per-session token, bind to `127.0.0.1`, and refuse unauthenticated connections.
- **Semantic tools, not pixels:** `snapshot`, `find`, `click(testId)`, `setText(testId, text)`, `send`, `waitFor(condition)`, `readMessages(conversationId)`. Return stable IDs, roles, names, state, and text.
- **Gate it:** automation off by default; enable per session; show an indicator; token auth; audit every call; no network exposure.
- **Version the contract:** tool names, arguments, and descriptions are API. Version them and keep them stable.
- **Do not replace accessibility:** the MCP is an additional surface. Screen readers and other UIA/AX clients still need the tree.
- **Fallback:** apps without an MCP remain controllable through OS accessibility, at lower reliability.

---

## 4. Pair screenshots with the tree

- Use both by default. The image handles icon-only controls, layout, and before/after verification; the tree handles text, roles, and actions.
- Use before/after screenshots at decision points: open conversation, paste, send. This catches a mis-targeted click that the tree alone would miss.
- Keep animations and layout shifts low so a snapshot and a screenshot agree.
- Return the image scale and source bounds with every screenshot so coordinates map back.

---

## 5. Test the tree as an API

Add OS-level smoke tests that assert the tree, not just the UI:

1. Send is a `Button` with a non-empty accessible name and `InvokePattern`, enabled.
2. The composer is an `Edit` (or a documented fallback) with a writable `ValuePattern`.
3. Message text nodes exist within your target depth and have non-empty names.
4. The open conversation exposes selection state (`SelectionItemPattern` or a documented equivalent).
5. The window title stays constant across unread changes.
6. At 100/150/200% DPI, UIA bounds map 1:1 to a `PrintWindow` capture.
7. On cold launch, the tree is non-empty without relying on a screen reader.

Tooling: axe-core and `eslint-plugin-jsx-a11y` in CI; Playwright's Electron/CDP support for DOM-level tests; FlaUI (Windows UIA) and macOS Accessibility Inspector/VoiceOver for platform-level checks.

---

## 6. Corrections to the common advice

These are the places the original advice was too strong or imprecise:

1. **DOM depth is not UIA depth.** Chromium inserts layers. "Keep controls within 10–15 levels" is a DOM guideline; the UIA tree will be deeper. Measure it.
2. **`role="log"` is a live region, not list semantics.** Use `list`/`listitem` for messages, and `option` + `aria-selected` for selectable conversations.
3. **`aria-current` does not reliably map to selection.** Use `aria-selected` with a role that supports it, and verify the pattern.
4. **Native text fields are not universally settable.** A `<textarea>` can surface as a read-only `Document` on some Chromium/Electron versions. Verify `ValuePattern`; paste is the fallback.
5. **`setAccessibilitySupportEnabled` is macOS/Windows, after `ready`, off by default, and costly.** Do not enable it globally without measuring; make it an automation mode.
6. **`--force-renderer-accessibility` is version-sensitive.** Use it in test builds, verify the tree, and keep names correct.
7. **Keeping all offscreen nodes mounted fights virtualization.** Expose counts and scroll patterns instead.
8. **A localhost socket without auth is a security hole.** Prefer stdio MCP, or loopback with a per-session token.
9. **The 1:1 screenshot/UIA mapping was measured at one DPI.** Test the scales you support.
10. **"Send is a Button" only helps if it is named.** An unnamed icon button still forces geometry/vision. Name it.

---

## 7. Checklist

- [ ] Every interactive control has a stable, unique accessible name.
- [ ] Icon-only buttons have `aria-label`; SVGs are `aria-hidden`.
- [ ] Editable fields are native or verified `contenteditable`; paste fallback is known.
- [ ] Message text is real DOM text, not canvas/CSS-generated content.
- [ ] Lists use list/listitem; selectable items use `aria-selected` and expose selection.
- [ ] States are explicit (`aria-expanded`, `aria-checked`, `aria-disabled`, `disabled`).
- [ ] Key controls have stable IDs that surface as `AutomationId`.
- [ ] Main window title is stable; unread counts are child text.
- [ ] The tree is non-empty on cold launch, or the client retries.
- [ ] The tree is verified at 100/150/200% DPI.
- [ ] Screenshots and the tree are paired for verification.
- [ ] An opt-in MCP server exists for agent control, with token auth and audit.

---

## Provenance and caveats

- Feishu spike: `docs/spikes/2026-11-10-windows-feishu.md` and `docs/spikes/windows-feishu.txt`.
- Electron `app.setAccessibilitySupportEnabled`: Electron API docs (macOS/Windows, disabled by default, performance warning).
- The Feishu version measured was 8.4.0.18 (the installed package is 8.5.6); re-measure if the app build changes.
- This guide is intentionally opinionated. Verify pattern mappings against the Electron/Chromium versions you actually ship.
