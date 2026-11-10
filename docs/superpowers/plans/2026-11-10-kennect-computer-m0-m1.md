# Kennect Computer — M0 Spikes and M1 Skeleton Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove Feishu accessibility coverage on Windows and macOS, then ship the first working Windows slice: an MCP server and desktop host that authenticate over a local pipe, list windows, select and focus a window, snapshot its accessibility tree and image, and stop — with tray, hotkey, single-session lock, config, and audit.

**Architecture:** A TypeScript MCP server speaks JSON-RPC 2.0 over a Windows named pipe to a .NET 10 host. The host owns Win32/UIA/capture; the server owns tools, policy, and audit. The protocol lives in a shared schema package and is platform-neutral — opaque window IDs, generic node/action vocabulary — so the macOS host can implement the same methods later.

**Tech Stack:** Node 24 (>=20), TypeScript strict, `@modelcontextprotocol/sdk`, zod, vitest; .NET 10 (`net10.0-windows`), C#, FlaUI.UIA3, WinForms, xUnit; Swift/AppKit + ApplicationServices for the macOS spike.

**Spec:** `DESIGN.md`

## Plan series

This is Plan 1 of 5. Each plan produces working, testable software on its own.

1. **This plan — M0 spikes + M1 skeleton (Windows).** Spikes, protocol, IPC, status/windows/select/snapshot/focus/stop, tray, hotkey, lock, config, audit.
2. **M2 core actions.** click/type(paste)/key/scroll/find/wait/read_text, `computer_batch`, stale-target and focus guards, coordinate spaces.
3. **M3 Feishu flow.** End-to-end reply, composition, pre-Send assertion, acceptance test.
4. **M4 complete surface.** drag/hover/move/window/clipboard/launch/OCR/multi-monitor, packaging and install.
5. **M5 macOS parity.** Swift host on the same protocol and acceptance suite.

## Global Constraints

- Node 20+ (development machine: Node 24.13); TypeScript strict; npm workspaces.
- .NET 10 SDK (`10.0.102` installed); target framework `net10.0-windows`; nullable enabled; implicit usings enabled.
- The Windows host is a per-user process, never a service; it must run in the interactive session.
- Protocol version is `1`. JSON-RPC 2.0, one JSON object per frame, 4-byte little-endian length prefix, UTF-8, max frame 16 MiB.
- The host writes `%LOCALAPPDATA%\KennectComputer\session.token` (32 random bytes, base64url, current-user-only ACL) and `%LOCALAPPDATA%\KennectComputer\session.json` (`{ pipeName, pid, protocolVersion, startedAt }`) at every start; both rotate per host start.
- Pipe name is `\\.\pipe\kennect-computer-<sanitized user>` and is published in `session.json`; the MCP server never guesses it.
- Window IDs are opaque strings (`w_<counter>`); native handles never appear in protocol payloads or tool output. Window identity is handle + process + class/bundle; title is metadata.
- Snapshot defaults: `maxDepth` 12, `maxNodes` 1500, `imageLongEdge` 1568, `maxImageLongEdge` 2576.
- Stop hotkey: `Ctrl+Alt+Shift+Esc`, registered with `RegisterHotKey`, consumed by the host.
- Audit log: `%LOCALAPPDATA%\KennectComputer\audit\YYYY-MM-DD.jsonl`, 7-day retention, text length/hash only — never message or field content.
- Default exclusions (process): `KeePass.exe`, `1Password.exe`, `Bitwarden.exe`, `WindowsSecurity.exe`, `LogonUI.exe`; titles: `*password*`, `*bank*`.
- `Feishu` is `preAuthorized` in config; acceptance only ever targets a dedicated test group or self-chat.
- The user's instruction authorizes the action; there is no interactive gate on instructed actions. Per-app authorization remains for non-preAuthorized apps.
- Input is serialized by the host; read-only calls may run concurrently. Input actions are never executed in parallel.
- Error codes: `STALE_SNAPSHOT`, `WINDOW_GONE`, `FOCUS_REFUSED`, `DESKTOP_UNAVAILABLE`, `PERMISSION_MISSING`, `HOST_UNAVAILABLE`, `APP_NOT_RESPONDING`, `EXPECTATION_FAILED`, `LOCKED_BY_OTHER_SESSION`, `AUTHORIZATION_DENIED`, `INVALID_REQUEST`, `UNAUTHORIZED`, `METHOD_NOT_FOUND`, `ACTION_TIMEOUT`, `FRAME_TOO_LARGE`. Every tool result carries `ok`/error code + message.
- Commit messages carry no Claude/Anthropic attribution. The user has explicitly forbidden it.
- macOS spike target: macOS 14+; the development MacBook runs macOS 26.6.1 on Apple silicon with Xcode and Claude Code.

## Review Focus

These are the failure modes most likely to bite a user, and the task whose tests pin each one:

1. **A window dies between selection and the next snapshot/focus** — expected: `WINDOW_GONE`, selection cleared, no input. Pinned in Task 7.
2. **The accessibility tree is empty on first attach but populates after focus** — expected: one retry after focusing, then a warning-bearing snapshot, never a hang. Pinned in Task 8.
3. **A second MCP session connects while another holds control** — expected: `LOCKED_BY_OTHER_SESSION`, no interleaved control. Pinned in Task 10.
4. **The host dies mid-request** — expected: `HOST_UNAVAILABLE`, exactly one respawn attempt, no duplicate execution. Pinned in Task 5.
5. **The token file is missing, stale, or unreadable** — expected: `UNAUTHORIZED`, fail closed, no unauthenticated control. Pinned in Tasks 4 and 5.

---

### Task 1: Windows Feishu accessibility spike (throwaway)

**Files:**
- Create: `spikes/windows-feishu/KennectComputer.Spike.Windows.csproj`
- Create: `spikes/windows-feishu/Program.cs`
- Create: `spikes/README.md`
- Create: `docs/spikes/2026-11-10-windows-feishu.md`
- Create: `docs/spikes/windows-feishu.png`
- Create: `docs/spikes/windows-feishu.txt`

**Interfaces:**
- Consumes: Feishu 8.5.6 at `%LOCALAPPDATA%\Feishu\Feishu.exe`, logged in on a test conversation.
- Produces: a report answering four measured questions that fix Task 8's node vocabulary — (1) total UIA nodes and interactive nodes; (2) whether message text is exposed, and via which property; (3) whether the composer is an Edit/Document and whether ValuePattern works; (4) whether Send is a Button with InvokePattern. This project is throwaway and is never referenced by the product.

- [ ] **Step 1: Create the project**

Run:
```powershell
dotnet new console -n KennectComputer.Spike.Windows -o spikes/windows-feishu
```
Edit the `.csproj`: set `<TargetFramework>net10.0-windows</TargetFramework>`, `<UseWindowsForms>true</UseWindowsForms>`, and add `<PackageReference Include="FlaUI.UIA3" Version="4.*" />` and `<PackageReference Include="System.Drawing.Common" Version="9.*" />`.

- [ ] **Step 2: Implement the probe**

`Program.cs` must:
1. `EnumWindows` + `GetWindowThreadProcessId`; find top-level windows whose process image is `Feishu.exe`.
2. Print each window: `HWND`, title, class, bounds, pid.
3. Restore and foreground the main Feishu window (`ShowWindow(SW_RESTORE)`, `SetForegroundWindow`), wait 1 s.
4. Attach FlaUI.UIA3 to the window; walk the raw tree to depth 20 with a node cap of 5000; count nodes by `ControlType`; print the first 50 nodes with role/name/automation id/value/bounds.
5. Search the tree for candidate chat-list items, message items, composer, and Send: print nodes whose Name contains the test conversation's name, whose `ControlType` is `Edit`/`Document`, or whose Name is `Send`/`发送`.
6. Capture the window with `PrintWindow(PW_RENDERFULLCONTENT)` and save it to `docs/spikes/windows-feishu.png`.

- [ ] **Step 3: Run it against Feishu**

Open Feishu on the dedicated test conversation, then run:
```powershell
dotnet run --project spikes/windows-feishu
```
Save the console output to `docs/spikes/windows-feishu.txt`.

- [ ] **Step 4: Write the report**

Create `docs/spikes/2026-11-10-windows-feishu.md` with a table per question and a short verdict: is semantic targeting viable, and which fallback (bounds-center, OCR, vision) is needed for which element. Record exact node counts and the screenshot path.

- [ ] **Step 5: Commit**

```bash
git add spikes/ docs/spikes/
git commit -m "spike: measure Feishu UIA coverage on Windows"
```

### Task 2: macOS Feishu accessibility spike (throwaway, run on the MacBook)

**Files:**
- Create: `spikes/macos-feishu/main.swift`
- Create: `docs/spikes/2026-11-10-macos-feishu.md`
- Create: `docs/spikes/macos-feishu.png`
- Create: `docs/spikes/macos-feishu.txt`

**Interfaces:**
- Consumes: Feishu for macOS, logged in on the same test conversation; Xcode/Swift toolchain; Claude Code running on the MacBook.
- Produces: the same four answers as Task 1, using AX + ScreenCaptureKit/screencapture. This task runs on the MacBook, not the Windows machine. Do it before Task 3 freezes the node vocabulary.

**Execution note:** If you are executing this plan on Windows, complete Task 1, then run Task 2 on the Mac via Claude Code before starting Task 3. The protocol vocabulary may change once Task 2's report exists.

- [ ] **Step 1: Confirm the toolchain**

Run on the Mac:
```bash
xcode-select -p
swift --version
sw_vers
uname -m
```
Expected: Xcode path, Swift version, macOS 26.6.1, `arm64`.

- [ ] **Step 2: Implement the probe**

`spikes/macos-feishu/main.swift` must:
1. Use `CGWindowListCopyWindowInfo` to find Feishu windows (owner name `Feishu`, layer 0, nonzero bounds); print window number, owner, title, bounds.
2. Get the app's `AXUIElement` for the Feishu process; walk `kAXChildrenAttribute` to depth 20 with a 5000-node cap; print role/title/description/identifier/value/bounds; count by role.
3. Search for the test conversation's name, the message list elements, `AXTextArea`/`AXTextField` composer candidates, and buttons titled `Send`/`发送`. Print whether `AXPress`/`AXSetValue`/`AXValue` are supported.
4. Capture the window:
```bash
screencapture -x -l <windowNumber> docs/spikes/macos-feishu.png
```

- [ ] **Step 3: Grant permissions**

Run the probe. When macOS prompts, grant Accessibility and Screen Recording to the terminal/Claude Code process. If a prompt does not appear but the API returns `kAXErrorAPIDisabled` or a black image, open System Settings → Privacy & Security and grant them manually; record what was needed.

- [ ] **Step 4: Run it against Feishu**

Run:
```bash
swift spikes/macos-feishu/main.swift > docs/spikes/macos-feishu.txt
```
Expected: a node dump and a screenshot file.

- [ ] **Step 5: Write the report and commit**

Create `docs/spikes/2026-11-10-macos-feishu.md` with the same four answers and a verdict. Then:
```bash
git add spikes/macos-feishu docs/spikes/
git commit -m "spike: measure Feishu AX coverage on macOS"
```

### Task 3: Workspace and shared protocol schema

**Files:**
- Create: `package.json`
- Create: `tsconfig.base.json`
- Modify: `.gitignore` (append `node_modules/`, `dist/`, `.mcp.json`, `bin/`, `obj/`)
- Create: `packages/shared/package.json`
- Create: `packages/shared/tsconfig.json`
- Create: `packages/shared/src/protocol.ts`
- Create: `packages/shared/src/schemas.ts`
- Create: `packages/shared/fixtures/status.json`
- Create: `packages/shared/fixtures/snapshot.json`
- Create: `packages/shared/test/protocol.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces: `PROTOCOL_VERSION = 1`; TS types `Rect`, `WindowInfo`, `Node`, `ImagePayload`, `Snapshot`, `MonitorInfo`, `HostStatus`, `HelloParams`, `HelloResult`, `ErrorCode`; zod schemas `RectSchema`, `WindowInfoSchema`, `NodeSchema`, `ImagePayloadSchema`, `SnapshotSchema`, `HostStatusSchema`, `HelloParamsSchema`, `ErrorCodeSchema`.

- [ ] **Step 1: Write the failing tests**

```ts
import { describe, it, expect } from "vitest";
import { HelloParamsSchema, NodeSchema, SnapshotSchema, WindowInfoSchema } from "../src/schemas";
import snapshotFixture from "../fixtures/snapshot.json";

describe("protocol schemas", () => {
  it("accepts a 32-byte base64url token and rejects a missing one", () => {
    expect(() => HelloParamsSchema.parse({ token: "a".repeat(43), protocolVersion: 1, clientName: "claude" })).not.toThrow();
    expect(() => HelloParamsSchema.parse({ protocolVersion: 1, clientName: "claude" })).toThrow();
  });
  it("rejects a snapshot node without a nodeId", () => {
    const bad = { ...snapshotFixture, nodes: [{ role: "button", name: "Send", bounds: { x: 0, y: 0, width: 10, height: 10 }, enabled: true, focused: false, actions: ["press"] }] };
    expect(() => SnapshotSchema.parse(bad)).toThrow();
  });
  it("rejects a native handle instead of exposing it", () => {
    expect(() => WindowInfoSchema.parse({ ...(snapshotFixture as any).window, hwnd: 1234 })).toThrow();
  });
  it("requires enabled, focused, and actions on every node", () => {
    expect(() => NodeSchema.parse({ nodeId: "n1:1", role: "button", name: "Send", bounds: { x: 0, y: 0, width: 1, height: 1 } })).toThrow();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test --workspace packages/shared`
Expected: FAIL because `src/schemas.ts` does not exist.

- [ ] **Step 3: Implement the workspace and schemas**

Root `package.json` uses npm workspaces `["packages/shared", "packages/mcp-server"]` with `build` and `test` scripts; add `zod` to `packages/shared/package.json` and `vitest` to the root dev dependencies; `tsconfig.base.json` sets `strict: true`, `target: ES2022`, `module: NodeNext`, `moduleResolution: NodeNext`. `protocol.ts` declares the interfaces exactly as listed in **Interfaces**; `schemas.ts` defines the zod schemas with `.strict()` on `WindowInfo`, `Node`, and `Snapshot` so unknown fields are rejected rather than silently accepted. Fixtures: `status.json` contains `hostVersion`, `protocolVersion`, `platform`, `locked`, `monitors`, and `permissions`; `snapshot.json` contains a full `Snapshot` with a `window` and two nodes.

- [ ] **Step 4: Run to verify pass**

Run: `npm test --workspace packages/shared`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add package.json tsconfig.base.json .gitignore packages/shared
git commit -m "feat: add shared protocol schema"
```

### Task 4: Host IPC, token auth, and status

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/KennectComputer.Host.csproj`
- Create: `packages/host-windows/KennectComputer.Host/Program.cs`
- Create: `packages/host-windows/KennectComputer.Host/Protocol/Dto.cs`
- Create: `packages/host-windows/KennectComputer.Host/Ipc/FrameCodec.cs`
- Create: `packages/host-windows/KennectComputer.Host/Ipc/PipeServer.cs`
- Create: `packages/host-windows/KennectComputer.Host/Ipc/JsonRpcDispatcher.cs`
- Create: `packages/host-windows/KennectComputer.Host/Auth/TokenStore.cs`
- Create: `packages/host-windows/KennectComputer.Host/Windows/IMonitorProvider.cs`
- Create: `packages/host-windows/KennectComputer.Host/Windows/IDesktopState.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/KennectComputer.Host.Tests.csproj`
- Create: `packages/host-windows/KennectComputer.Host.Tests/FrameCodecTests.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/TokenStoreTests.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/DispatcherTests.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/DtoConformanceTests.cs`

**Interfaces:**
- Consumes: `packages/shared/fixtures/status.json` (conformance test).
- Produces: `FrameCodec.WriteAsync(Stream, ReadOnlyMemory<byte>, CancellationToken)`; `FrameCodec.ReadAsync(Stream, CancellationToken) -> Task<byte[]?>` (null on EOF; throws `FrameTooLargeException` above 16 MiB); `TokenStore.Rotate() -> string`, `Validate(string) -> bool`, `TokenPath`, `SessionInfoPath`; `PipeServer(string pipeName, TokenStore, JsonRpcDispatcher)` with `RunAsync(CancellationToken)`; `JsonRpcDispatcher.HandleAsync(JsonElement) -> Task<JsonElement>`; `hello` params `{ token, protocolVersion, clientName }`, result `{ hostVersion, protocolVersion, platform, pid }`; `status` result `{ hostVersion, protocolVersion, platform, locked, monitors, permissions }`, where `permissions` is `{ accessibility: bool, screenRecording: bool }` (both true on Windows). `locked` is computed through `IDesktopState.IsLocked()` (compare the input desktop with the default desktop); tests inject a fake.

- [ ] **Step 1: Write the failing tests**

```csharp
// FrameCodecTests.cs
[Fact]
public async Task ReadsAFragmentedFrame()
{
    var payload = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}");
    var stream = new OneByteAtATimeStream(FrameCodec.Frame(payload));
    var read = await FrameCodec.ReadAsync(stream, CancellationToken.None);
    Assert.Equal(payload, read);
}

[Fact]
public async Task RejectsAnOversizedFrame()
{
    var header = BitConverter.GetBytes(16 * 1024 * 1024 + 1);
    var stream = new MemoryStream(header);
    await Assert.ThrowsAsync<FrameTooLargeException>(() => FrameCodec.ReadAsync(stream, CancellationToken.None));
}

// TokenStoreTests.cs
[Fact]
public void RotatesA32ByteTokenAndRejectsTheOldOne()
{
    var store = TokenStore.CreateForTests(tempDir);
    var first = store.Rotate();
    var second = store.Rotate();
    Assert.Equal(32, Base64Url.Decode(first).Length);
    Assert.False(store.Validate(first));
    Assert.True(store.Validate(second));
}

// DispatcherTests.cs
[Fact]
public async Task HelloWithValidTokenSucceedsAndStatusReportsWindows()
{
    var tokens = TokenStore.CreateForTests(tempDir);
    var dispatcher = new JsonRpcDispatcher(tokens, new FakeMonitorProvider(), new FakeDesktopState());
    var token = tokens.Rotate();
    var hello = await dispatcher.HandleAsync(JsonDocument.Parse($$"""{"jsonrpc":"2.0","id":1,"method":"hello","params":{"token":"{{token}}","protocolVersion":1,"clientName":"test"}}""").RootElement);
    Assert.Equal("windows", hello.GetProperty("result").GetProperty("platform").GetString());
    var status = await dispatcher.HandleAsync(JsonDocument.Parse("""{"jsonrpc":"2.0","id":2,"method":"status","params":{}}""").RootElement);
    Assert.Equal("windows", status.GetProperty("result").GetProperty("platform").GetString());
}

[Fact]
public async Task HelloWithAStaleTokenReturnsUnauthorized()
{
    var tokens = TokenStore.CreateForTests(tempDir);
    var dispatcher = new JsonRpcDispatcher(tokens, new FakeMonitorProvider(), new FakeDesktopState());
    tokens.Rotate();
    var response = await dispatcher.HandleAsync(JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"hello","params":{"token":"wrong","protocolVersion":1,"clientName":"test"}}""").RootElement);
    Assert.Equal("UNAUTHORIZED", response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
}

[Fact]
public async Task StatusReportsALockedDesktop()
{
    var tokens = TokenStore.CreateForTests(tempDir);
    var dispatcher = new JsonRpcDispatcher(tokens, new FakeMonitorProvider(), new FakeDesktopState { Locked = true });
    var response = await dispatcher.HandleAsync(JsonDocument.Parse("""{"jsonrpc":"2.0","id":2,"method":"status","params":{}}""").RootElement);
    Assert.True(response.GetProperty("result").GetProperty("locked").GetBoolean());
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the host skeleton**

`Program.cs` sets `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`, creates `TokenStore`, rotates the token, writes `session.json`, starts `PipeServer` on a background task, and blocks until Ctrl+C. `TokenStore` writes the token with a current-user-only ACL and the session file with the sanitized pipe name. `JsonRpcDispatcher` handles `hello`, `status`, and rejects unknown methods with `METHOD_NOT_FOUND`; `hello` with a bad token returns `UNAUTHORIZED` and closes the connection. `DtoConformanceTests` deserializes `packages/shared/fixtures/status.json` into the C# `HostStatus` DTO and re-serializes it; assert the JSON keys and values round-trip exactly (camelCase policy).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows
git commit -m "feat: add Windows host IPC, token auth, and status"
```

### Task 5: MCP server transport and `computer_status`

**Files:**
- Create: `packages/mcp-server/package.json`
- Create: `packages/mcp-server/tsconfig.json`
- Create: `packages/mcp-server/src/index.ts`
- Create: `packages/mcp-server/src/host-client.ts`
- Create: `packages/mcp-server/src/host-manager.ts`
- Create: `packages/mcp-server/src/config.ts`
- Create: `packages/mcp-server/test/host-client.test.ts`
- Create: `packages/mcp-server/test/host-manager.test.ts`
- Create: `packages/mcp-server/test/status-tool.test.ts`

**Interfaces:**
- Consumes: `session.json` and `session.token` from the host; protocol types from `@kennect/shared`.
- Produces: `HostClient(pipePath, tokenPath)` with `connect(): Promise<HelloResult>`, `request<T>(method, params, timeoutMs = 10_000): Promise<T>`, `close(): void`; `HostManager(options)` with `ensureHost(): Promise<HostClient>`; `statusTool(host): Promise<HostStatus>`; MCP tool `computer_status` returning the `HostStatus` JSON.

- [ ] **Step 1: Write the failing tests**

```ts
it("correlates responses and rejects on timeout", async () => {
  const fake = await startFakeHost({ respondToStatus: false });
  const client = new HostClient(fake.pipePath, fake.tokenPath);
  await client.connect();
  await expect(client.request("status", {}, 50)).rejects.toMatchObject({ code: "ACTION_TIMEOUT" });
});

it("fails closed when the token is missing", async () => {
  const fake = await startFakeHost({ tokenPath: join(tmp, "missing.token") });
  const client = new HostClient(fake.pipePath, fake.tokenPath);
  await expect(client.connect()).rejects.toMatchObject({ code: "UNAUTHORIZED" });
});

it("does not replay an in-flight request after the host drops", async () => {
  const fake = await startFakeHost({ dropAfterRequest: "status" });
  const client = new HostClient(fake.pipePath, fake.tokenPath);
  await client.connect();
  await expect(client.request("status", {})).rejects.toMatchObject({ code: "HOST_UNAVAILABLE" });
  expect(fake.statusRequests).toBe(1);
});

it("respawns the host once when the first connection fails", async () => {
  const spawn = vi.fn().mockResolvedValue(undefined);
  let attempts = 0;
  const manager = new HostManager({ spawn, readSession: () => null, connect: async () => { if (attempts++ === 0) throw new Error("refused"); return fakeClient; } });
  await expect(manager.ensureHost()).resolves.toBe(fakeClient);
  expect(spawn).toHaveBeenCalledTimes(1);
});
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test --workspace packages/mcp-server`
Expected: FAIL — modules do not exist.

- [ ] **Step 3: Implement the client, manager, and status tool**

`HostClient` connects with `net.connect(pipePath)`, sends `hello` with the token read from `tokenPath`, validates `protocolVersion === 1`, and matches responses by JSON-RPC `id`; timeouts map to `ACTION_TIMEOUT`, connection failures to `HOST_UNAVAILABLE`, and auth failures to `UNAUTHORIZED`. `HostManager` reads `session.json`/`session.token`; if absent or the pipe is refused, spawns the host at `KENNECT_HOST_PATH`, waits up to 2 s for the files to appear, reconnects once, and never spawns a second time in the same call. `index.ts` registers `computer_status` with the MCP SDK and calls `host.request("status", {})`.

- [ ] **Step 4: Run to verify pass**

Run: `npm test --workspace packages/mcp-server`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/mcp-server
git commit -m "feat: add MCP server transport and computer_status"
```

### Task 6: Window enumeration and `computer_windows`

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Windows/IWindowApi.cs`
- Create: `packages/host-windows/KennectComputer.Host/Windows/WindowEnumerator.cs`
- Create: `packages/host-windows/KennectComputer.Host/Windows/WindowRegistry.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Ipc/JsonRpcDispatcher.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/WindowEnumeratorTests.cs`
- Create: `packages/mcp-server/src/tools/windows.ts`
- Create: `packages/mcp-server/test/windows-tool.test.ts`

**Interfaces:**
- Consumes: protocol `WindowInfo`/`Rect` shapes from Task 3 and the host DTOs from Task 4; `WindowRegistry` is created here.
- Produces: `WindowEnumerator(IWindowApi, WindowRegistry).List() -> IReadOnlyList<WindowInfo>`; `WindowRegistry.Register(RawWindow) -> WindowInfo`, `TryResolve(string windowId, out ResolvedWindow) -> bool`; host method `windows.list` returning `{ windows: WindowInfo[] }`; MCP tool `computer_windows` returning the same JSON.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void FiltersToolCloakedInvisibleAndUntitledWindows()
{
    var api = FakeWindowApi.With(
        normal: new RawWindow(1, 100, "Notepad", "Notepad", new Rect(0, 0, 800, 600)),
        tool: new RawWindow(2, 101, "Tool", "", new Rect(0, 0, 10, 10)) { ExStyle = 0x80 },
        cloaked: new RawWindow(3, 102, "Hidden", "", new Rect(0, 0, 10, 10)) { Cloaked = true },
        invisible: new RawWindow(4, 103, "Hidden", "", new Rect(0, 0, 10, 10)) { Visible = false },
        untitled: new RawWindow(5, 104, "", "", new Rect(0, 0, 10, 10)));
    var list = new WindowEnumerator(api, new WindowRegistry()).List();
    Assert.Single(list);
    Assert.Equal("Notepad", list[0].Title);
}

[Fact]
public void RegistryInvalidatesAWindowThatDied()
{
    var registry = new WindowRegistry();
    var info = registry.Register(new RawWindow(1, 100, "Notepad", "Notepad", new Rect(0, 0, 10, 10)));
    Assert.True(registry.TryResolve(info.WindowId, out _));
    registry.MarkDeadForTests(1);
    Assert.False(registry.TryResolve(info.WindowId, out _));
}
```

```ts
it("computer_windows sends windows.list and returns the array", async () => {
  const host = fakeHost({ "windows.list": { windows: [{ windowId: "w_1", title: "Notepad" }] } });
  const result = await windowsTool(host);
  expect(host.calls).toContainEqual(["windows.list", {}]);
  expect(result.windows[0].windowId).toBe("w_1");
});
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter Window`
Expected: FAIL.

- [ ] **Step 3: Implement enumeration and the MCP tool**

`WindowApi` wraps `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, `GetWindowRect`, `GetWindowText`, `GetClassName`, `GetWindowLongPtr(GWL_EXSTYLE)`, `GetForegroundWindow`, and `DwmGetWindowAttribute(DWMWA_CLOAKED)`. The enumerator filters invisible, cloaked, `WS_EX_TOOLWINDOW`, and untitled windows, assigns opaque IDs through the registry, and returns `windowId`, `title`, `processName`, `pid`, `bounds`, `focused`, `minimized`, `visible`. Integration test: start `notepad.exe`, wait for the main window, call `List()`, assert a window with process `notepad` and the launched pid exists; kill the process in `finally`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter Window` and `npm test --workspace packages/mcp-server`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows packages/mcp-server
git commit -m "feat: enumerate windows and add computer_windows"
```

### Task 7: Selection, focus, and `computer_select` / `computer_focus`

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Windows/ForegroundService.cs`
- Create: `packages/host-windows/KennectComputer.Host/Policy/IAppAuthorization.cs`
- Create: `packages/host-windows/KennectComputer.Host/Policy/AllowAllAuthorization.cs`
- Create: `packages/host-windows/KennectComputer.Host/Control/SelectionStore.cs`
- Create: `packages/host-windows/KennectComputer.Host/Control/SelectionService.cs`
- Create: `packages/host-windows/KennectComputer.Host/Control/Results.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Ipc/JsonRpcDispatcher.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/FocusTests.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/SelectionTests.cs`
- Create: `packages/mcp-server/src/tools/select.ts`
- Create: `packages/mcp-server/src/tools/focus.ts`
- Create: `packages/mcp-server/test/select-tool.test.ts`

**Interfaces:**
- Consumes: `WindowRegistry` from Task 6; protocol shapes from Task 3. `IAppAuthorization` is created here.
- Produces: `ForegroundService.FocusAsync(nint hwnd, TimeSpan timeout) -> FocusResult { bool Ok, ErrorCode? Error }`; `IAppAuthorization.AuthorizeAsync(string processPath, CancellationToken) -> Task<AuthorizationDecision>` with `AllowOnce`, `AllowSession`, `Deny`; `SelectionService.SelectAsync(string windowId, CancellationToken) -> SelectResult { WindowInfo? Window, ErrorCode? Error }` and `FocusSelectedAsync(CancellationToken) -> SelectResult`; `SelectionStore.Select(WindowInfo)`, `Selected`; host methods `window.select` (`{ windowId? , process? , title? }` → `{ window }`) and `action` (`{ kind: "focus", windowId }` → `{ window }`); MCP tools `computer_select` and `computer_focus`. Node-level focus (focusing a control by `nodeId`) arrives with M2 actions.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task ReturnsFocusRefusedWhenForegroundCannotBeSet()
{
    var service = new ForegroundService(new FakeWin32 { ForegroundSucceeds = false });
    var result = await service.FocusAsync((nint)1, TimeSpan.FromMilliseconds(50));
    Assert.False(result.Ok);
    Assert.Equal(ErrorCode.FocusRefused, result.Error);
}

[Fact]
public async Task SelectingADeadWindowReturnsWindowGone()
{
    var registry = new WindowRegistry();
    var info = registry.Register(new RawWindow(1, 100, "Notepad", "Notepad", new Rect(0, 0, 10, 10)));
    registry.MarkDeadForTests(1);
    var result = await new SelectionService(registry, new AllowAllAuthorization(), new SelectionStore()).SelectAsync(info.WindowId, default);
    Assert.Equal(ErrorCode.WindowGone, result.Error);
}

[Fact]
public async Task FocusingAfterTheSelectedWindowDiedReturnsWindowGone()
{
    var registry = new WindowRegistry();
    var info = registry.Register(new RawWindow(1, 100, "Notepad", "Notepad", new Rect(0, 0, 10, 10)));
    var selection = new SelectionStore();
    selection.Select(info);
    registry.MarkDeadForTests(1);
    var result = await new SelectionService(registry, new AllowAllAuthorization(), selection).FocusSelectedAsync(default);
    Assert.Equal(ErrorCode.WindowGone, result.Error);
    Assert.Null(selection.Selected);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "Focus|Selection"`
Expected: FAIL.

- [ ] **Step 3: Implement selection and focus**

`ForegroundService.FocusAsync` calls `SetForegroundWindow` with the `AttachThreadInput` workaround, waits up to the timeout, and verifies `GetForegroundWindow` is the target; failure returns `FOCUS_REFUSED`. `window.select` resolves the ID, checks the registry identity (handle alive, process and class match), asks `IAppAuthorization`, focuses, stores the selection, and returns the `WindowInfo`. A dead handle clears the selection and returns `WINDOW_GONE`; denial returns `AUTHORIZATION_DENIED`. `action` accepts only `kind: "focus"` in this milestone. The MCP `computer_select` calls `window.select`; `computer_focus` calls `action`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "Focus|Selection"` and `npm test --workspace packages/mcp-server`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows packages/mcp-server
git commit -m "feat: add window selection and focus"
```

### Task 8: Accessibility snapshot and `computer_snapshot`

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Uia/SnapshotOptions.cs`
- Create: `packages/host-windows/KennectComputer.Host/Uia/NodeId.cs`
- Create: `packages/host-windows/KennectComputer.Host/Uia/TreeModel.cs`
- Create: `packages/host-windows/KennectComputer.Host/Uia/TreeSlicer.cs`
- Create: `packages/host-windows/KennectComputer.Host/Uia/UiaSnapshotter.cs`
- Create: `packages/host-windows/KennectComputer.Host/Uia/SnapshotStore.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Ipc/JsonRpcDispatcher.cs`
- Modify: `packages/mcp-server/src/tools/select.ts`
- Create: `packages/host-windows/KennectComputer.Host.Tests/TreeSlicerTests.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/SnapshotTests.cs`
- Create: `packages/mcp-server/src/tools/snapshot.ts`
- Create: `packages/mcp-server/test/snapshot-tool.test.ts`

**Interfaces:**
- Consumes: selected `WindowInfo`, `WindowRegistry`.
- Produces: `SnapshotMode { A11y, Vision, Both }`; `SnapshotOptions { MaxDepth = 12, MaxNodes = 1500, Image = false, ImageLongEdge = 1568, MaxImageLongEdge = 2576, Mode = Both }`; `NodeId.Create(int generation, int index) -> string`, `NodeId.TryParse(string, out int generation, out int index) -> bool`; `IUiaWalker`; `TreeSlicer.Slice(TreeModel, SnapshotOptions) -> SliceResult { IReadOnlyList<Node> Nodes, int TotalNodes, bool Truncated }`; `UiaSnapshotter(IUiaWalker walker).Snapshot(nint hwnd, SnapshotOptions, CancellationToken) -> Snapshot`; `SnapshotStore.Save(windowId, snapshot, nodeMap)`; host method `snapshot` (`{ windowId, mode, maxDepth, maxNodes, image }`) → `Snapshot`; MCP `computer_snapshot` returning a text outline plus the image content. `computer_select` is modified to call `window.select` then `snapshot` with `mode: "a11y"` and return `{ window, snapshot }`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void CapsNodesByDepthAndCount()
{
    var tree = TreeModel.Nested(depth: 20, childrenPerLevel: 3);
    var slice = TreeSlicer.Slice(tree, new SnapshotOptions { MaxDepth = 5, MaxNodes = 10 });
    Assert.True(slice.Truncated);
    Assert.True(slice.Nodes.Count <= 10);
}

[Fact]
public void RetriesOnceWhenTheFirstTreeIsEmpty()
{
    var walker = new FakeUiaWalker(emptyFirst: true);
    var snapshot = new UiaSnapshotter(walker).Snapshot(1, new SnapshotOptions(), CancellationToken.None);
    Assert.Equal(2, walker.Calls);
    Assert.Null(snapshot.Warning);
}

[Fact]
public void NodeIdsCarryTheSnapshotGeneration()
{
    var id = NodeId.Create(7, 3);
    Assert.True(NodeId.TryParse(id, out var generation, out var index));
    Assert.Equal(7, generation);
    Assert.Equal(3, index);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "Tree|Snapshot"`
Expected: FAIL.

- [ ] **Step 3: Implement the slicer, walker, and snapshot**

`UiaSnapshotter` attaches FlaUI.UIA3 to the HWND, issues a `CacheRequest` for Name, AutomationId, ControlType, BoundingRectangle, IsEnabled, IsOffscreen, HasKeyboardFocus, and supported patterns, walks the tree to the caps, maps nodes to `TreeModel`, and passes the model to `TreeSlicer`. `mode: "vision"` skips the walk and returns an empty node list; `"a11y"` and `"both"` walk. `modal` is true when a visible element exposes `WindowPattern.IsModal`. The slicer keeps interactive roles (Button, Edit, Document, Hyperlink, CheckBox, RadioButton, ComboBox, ListItem, MenuItem, TabItem, TreeItem, Slider, Spinner, Text) and text nodes, drops offscreen/disabled nodes, collapses unnamed containers, and marks `truncated`. Each snapshot increments a generation; node IDs are `n<generation>:<index>`; `SnapshotStore` keeps the node map for later actions. If the first walk yields zero nodes, focus the window and retry once; if still empty, return an empty-node snapshot with `warning: "accessibility tree empty; vision fallback required"`. `computer_snapshot` formats the header (`app`, `window`, `focused`, `modal`, `nodes shown/total`) and one line per node. Update `select.ts` so `computer_select` calls `window.select`, then `snapshot` with `mode: "a11y"`, and returns both.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "Tree|Snapshot"` and `npm test --workspace packages/mcp-server`
Expected: PASS. Integration: a Notepad snapshot has at least 10 interactive nodes and a focused node ID.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows packages/mcp-server
git commit -m "feat: add accessibility snapshot and computer_snapshot"
```

### Task 9: Window capture and snapshot image

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Capture/ScreenCapture.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Uia/UiaSnapshotter.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Ipc/JsonRpcDispatcher.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/ScreenCaptureTests.cs`
- Create: `packages/mcp-server/test/snapshot-image.test.ts`

**Interfaces:**
- Consumes: window bounds and `SnapshotOptions`.
- Produces: `ScreenCapture.Capture(nint hwnd, Rect bounds, int imageLongEdge, bool enabled = true) -> ImagePayload?`; `ScreenCapture.Encode(Bitmap bitmap, int imageLongEdge, Rect sourceBounds) -> ImagePayload`; `ImagePayload` is `{ data, mimeType: "image/png", scale, sourceBounds }`; `scale` is physical pixels per returned image pixel; `snapshot` includes the image only when `image: true`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void DownscalesToTheLongEdgeAndKeepsAspect()
{
    using var bitmap = new Bitmap(3000, 2000);
    var payload = ScreenCapture.Encode(bitmap, imageLongEdge: 1568, sourceBounds: new Rect(0, 0, 3000, 2000));
    using var image = new Bitmap(new MemoryStream(Convert.FromBase64String(payload.Data)));
    Assert.Equal(1568, Math.Max(image.Width, image.Height));
    Assert.Equal(3000.0 / 1568.0, payload.Scale, 2);
}

[Fact]
public void OmitsTheImageWhenNotRequested()
{
    var payload = ScreenCapture.Capture(hwnd: (nint)1, bounds: new Rect(0, 0, 100, 100), imageLongEdge: 1568, enabled: false);
    Assert.Null(payload);
}
```

```ts
it("returns image content when the host includes a PNG", async () => {
  const result = await snapshotTool(fakeHostWithImage()) as any;
  expect(result.content.some((c: any) => c.type === "image")).toBe(true);
});
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter ScreenCapture`
Expected: FAIL.

- [ ] **Step 3: Implement capture**

`ScreenCapture` uses `PrintWindow(PW_RENDERFULLCONTENT)` first; if it fails or returns an all-black bitmap, restores the window and falls back to `CopyFromScreen` over `bounds`. Downscale so the long edge is at most `imageLongEdge` (default 1568, clamped to 2576), encode PNG, and compute `scale = sourceBounds.Width / image.Width`. Attach the payload to `Snapshot.Image` when requested. Integration: capture Notepad, decode the PNG, assert dimensions and scale; assert `sourceBounds` equals the window bounds.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter ScreenCapture` and `npm test --workspace packages/mcp-server`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows packages/mcp-server
git commit -m "feat: capture window images in snapshots"
```

### Task 10: Tray, stop hotkey, single-session lock, authorization, and config

**Files:**
- Create: `packages/host-windows/KennectComputer.Host/Tray/TrayApp.cs`
- Create: `packages/host-windows/KennectComputer.Host/Input/StopHotkey.cs`
- Create: `packages/host-windows/KennectComputer.Host/Control/ControlSession.cs`
- Create: `packages/host-windows/KennectComputer.Host/Control/SingleInstance.cs`
- Create: `packages/host-windows/KennectComputer.Host/Policy/AppAuthorization.cs`
- Create: `packages/host-windows/KennectComputer.Host/Policy/IAuthorizationPrompt.cs`
- Create: `packages/host-windows/KennectComputer.Host/Policy/IExclusionPolicy.cs`
- Create: `packages/host-windows/KennectComputer.Host/Config/ConfigStore.cs`
- Create: `packages/host-windows/KennectComputer.Host/Config/AppConfig.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Program.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Ipc/JsonRpcDispatcher.cs`
- Modify: `packages/host-windows/KennectComputer.Host/Windows/WindowEnumerator.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/ControlSessionTests.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/AuthorizationTests.cs`
- Create: `packages/host-windows/KennectComputer.Host.Tests/ConfigTests.cs`
- Create: `packages/mcp-server/src/tools/stop.ts`
- Create: `packages/mcp-server/test/stop-tool.test.ts`
- Create: `config/default.json`

**Interfaces:**
- Consumes: `IAppAuthorization` from Task 7; config file.
- Produces: `ControlSession.TryAcquire(string sessionId) -> bool`, `Release(string sessionId)`, `Stop()`; `SingleInstance.TryAcquire() -> bool` (named mutex; a second host exits); `StopHotkey.Register(IntPtr hwnd, int id, ModifierKeys, Keys)` with `IHotkeyRegistrar`; `AppAuthorization(IAppConfig, IAuthorizationPrompt)` implementing `IAppAuthorization`; `IExclusionPolicy.IsExcluded(string processName, string title) -> bool`; `WindowEnumerator` gains an optional `IExclusionPolicy`; `ConfigStore.Load(string path) -> AppConfig`; `AppConfig` mirrors `config/default.json` (host, control, perception, privacy, apps); host method `stop` → `{ ok: true }`; MCP tool `computer_stop` calls `stop` and returns `{ ok: true }`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void SecondSessionIsRejected()
{
    var session = new ControlSession();
    Assert.True(session.TryAcquire("a"));
    Assert.False(session.TryAcquire("b"));
    session.Release("a");
    Assert.True(session.TryAcquire("b"));
}

[Fact]
public async Task PreAuthorizedAppSkipsThePrompt()
{
    var prompt = new FakeAuthorizationPrompt();
    var auth = new AppAuthorization(new AppConfig { Apps = { ["Feishu"] = new AppPolicy { PreAuthorized = true } } }, prompt);
    var decision = await auth.AuthorizeAsync("C:\\Apps\\Feishu\\Feishu.exe", default);
    Assert.Equal(AuthorizationDecision.AllowSession, decision);
    Assert.Equal(0, prompt.Calls);
}

[Fact]
public async Task DeniedAppIsDeniedForTheSession()
{
    var prompt = new FakeAuthorizationPrompt(AuthorizationDecision.Deny);
    var auth = new AppAuthorization(new AppConfig(), prompt);
    Assert.Equal(AuthorizationDecision.Deny, await auth.AuthorizeAsync("C:\\Apps\\Other\\Other.exe", default));
    Assert.Equal(AuthorizationDecision.Deny, await auth.AuthorizeAsync("C:\\Apps\\Other\\Other.exe", default));
    Assert.Equal(1, prompt.Calls);
}

[Fact]
public void ExcludedProcessesAreFilteredFromTheWindowList()
{
    var policy = new ExclusionPolicy(new AppConfig { Privacy = new PrivacyConfig { ExcludedProcesses = ["Feishu.exe"] } });
    var api = FakeWindowApi.With(normal: new RawWindow(1, 100, "Feishu.exe", "Feishu", new Rect(0, 0, 10, 10)));
    var list = new WindowEnumerator(api, new WindowRegistry(), policy).List();
    Assert.Empty(list);
}

[Fact]
public void SecondHostInstanceDoesNotAcquireTheMutex()
{
    var name = "kennect-test-" + Guid.NewGuid();
    using var first = new SingleInstance(name);
    Assert.True(first.TryAcquire());
    using var second = new SingleInstance(name);
    Assert.False(second.TryAcquire());
}

[Fact]
public void ExcludedTitlePatternsAreFiltered()
{
    var policy = new ExclusionPolicy(new AppConfig { Privacy = new PrivacyConfig { ExcludedTitles = ["*password*"] } });
    var api = FakeWindowApi.With(normal: new RawWindow(1, 100, "Chrome", "My password vault", new Rect(0, 0, 10, 10)));
    var list = new WindowEnumerator(api, new WindowRegistry(), policy).List();
    Assert.Empty(list);
}
```

```ts
it("computer_stop sends stop to the host", async () => {
  const host = fakeHost({ stopResult: { ok: true } });
  await expect(stopTool(host)).resolves.toEqual({ ok: true });
  expect(host.calls).toContainEqual(["stop", {}]);
});
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "Control|Authorization|Config"`
Expected: FAIL.

- [ ] **Step 3: Implement the tray, hotkey, lock, authorization, and config**

`TrayApp` creates a `NotifyIcon` with Status, Stop, and Exit items and a tooltip that reflects idle/controlled state; it runs on the STA main thread. `StopHotkey` registers `Ctrl+Alt+Shift+Esc` on a hidden message window and calls `ControlSession.Stop()`, which cancels the current operation's `CancellationTokenSource`; the key is consumed. `hello` acquires the control session; a second session gets `LOCKED_BY_OTHER_SESSION`; disconnect releases it. `SingleInstance` holds a named mutex; a second host brings up the existing tray and exits. The `stop` JSON-RPC method calls `ControlSession.Stop()`. `AppAuthorization` skips the prompt for `preAuthorized` apps (matching the config key to the process name, case-insensitive), caches decisions per process path for the session, and otherwise shows a WinForms dialog with Allow once / Allow for session / Deny. `ExclusionPolicy` implements `privacy.excludedProcesses` and `privacy.excludedTitles` and is passed to `WindowEnumerator`. `ConfigStore` loads `config/default.json` and overlays `%LOCALAPPDATA%\KennectComputer\config.json`; `KENNECT_COMPUTER_CONFIG` overrides the default path.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test packages/host-windows/KennectComputer.Host.Tests --filter "Control|Authorization|Config"` and `npm test --workspace packages/mcp-server`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add packages/host-windows config/default.json
git commit -m "feat: add tray, stop hotkey, control lock, and authorization"
```

### Task 11: Audit log, install scripts, and end-to-end M1

**Files:**
- Create: `packages/mcp-server/src/audit/log.ts`
- Create: `packages/mcp-server/test/audit.test.ts`
- Modify: `packages/mcp-server/src/index.ts`
- Create: `scripts/install-dev.ps1`
- Create: `scripts/run-dev.ps1`
- Create: `README.md`
- Create: `.mcp.json` (generated by the install script, gitignored)

**Interfaces:**
- Consumes: all six tools.
- Produces: `AuditLog({ dir, retentionDays = 7, now? })` with `write(entry: AuditEntry): void`; `AuditEntry` fields `timestamp`, `sessionId`, `tool`, `windowId?`, `process?`, `nodeId?`, `result`, `durationMs`; one JSONL file per UTC day; files older than the retention window are deleted on start; no text content. `scripts/install-dev.ps1` builds both packages and writes `.mcp.json` with `command: node`, `args: [<abs path>/packages/mcp-server/dist/index.js]`, and env `KENNECT_HOST_PATH`, `KENNECT_COMPUTER_CONFIG`.

- [ ] **Step 1: Write the failing tests**

```ts
it("writes one JSONL line with no field content", () => {
  const log = new AuditLog({ dir: tmp, retentionDays: 7, now: () => new Date("2026-11-10T12:00:00Z") });
  log.write({ sessionId: "s1", tool: "computer_snapshot", windowId: "w_1", process: "Feishu.exe", result: "ok", durationMs: 12 });
  const line = JSON.parse(readFileSync(join(tmp, "2026-11-10.jsonl"), "utf8").trim());
  expect(line.tool).toBe("computer_snapshot");
  expect(line).not.toHaveProperty("text");
  expect(line).not.toHaveProperty("content");
});

it("deletes files older than the retention window", () => {
  const old = join(tmp, "2026-11-01.jsonl");
  writeFileSync(old, "{}\n");
  new AuditLog({ dir: tmp, retentionDays: 7, now: () => new Date("2026-11-10T00:00:00Z") });
  expect(existsSync(old)).toBe(false);
});
```

- [ ] **Step 2: Run to verify failure**

Run: `npm test --workspace packages/mcp-server`
Expected: FAIL.

- [ ] **Step 3: Implement audit, wiring, and scripts**

Wrap every tool handler with `audit.write` in a `finally`, recording the tool, target window/process, node ID when present, result code, and duration. `scripts/install-dev.ps1` runs `npm install`, `npm run build`, `dotnet build packages/host-windows`, writes `.mcp.json`, and prints the recommended Claude Code allowlist for the six tools. `scripts/run-dev.ps1` starts the host and prints the MCP registration status. `README.md` documents the dev loop and the test conversation requirement.

- [ ] **Step 4: Run to verify pass**

Run: `npm test --workspace packages/mcp-server` and `dotnet test packages/host-windows/KennectComputer.Host.Tests`
Expected: PASS.

- [ ] **Step 5: End-to-end check**

Build, start the host (tray visible), and confirm Claude Code sees `computer_status`, `computer_windows`, `computer_select`, `computer_snapshot`, `computer_focus`, and `computer_stop`. Call `computer_status`, then `computer_windows`, select Notepad, snapshot it (a11y + image), and press the stop hotkey during a long snapshot. Confirm the audit line is written and contains no text content.

- [ ] **Step 6: Commit**

```bash
git add packages/mcp-server scripts README.md
git commit -m "feat: add audit log, install scripts, and end-to-end M1"
```
