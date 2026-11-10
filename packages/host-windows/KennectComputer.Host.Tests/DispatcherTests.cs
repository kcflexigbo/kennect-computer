using System.Text.Json;
using KennectComputer.Host.Auth;
using KennectComputer.Host.Control;
using KennectComputer.Host.Ipc;
using KennectComputer.Host.Policy;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

public sealed class DispatcherTests
{
    [Fact]
    public async Task HelloWithValidTokenSucceedsAndStatusReportsWindows()
    {
        var tempDir = NewTempDir();
        var tokens = TokenStore.CreateForTests(tempDir);
        var dispatcher = NewDispatcher(tokens);
        var token = tokens.Rotate();
        var hello = await dispatcher.HandleAsync(JsonDocument.Parse($$$"""{"jsonrpc":"2.0","id":1,"method":"hello","params":{"token":"{{{token}}}","protocolVersion":1,"clientName":"test"}}""").RootElement);
        Assert.Equal("windows", hello.GetProperty("result").GetProperty("platform").GetString());
        var status = await dispatcher.HandleAsync(JsonDocument.Parse("""{"jsonrpc":"2.0","id":2,"method":"status","params":{}}""").RootElement);
        Assert.Equal("windows", status.GetProperty("result").GetProperty("platform").GetString());
    }

    [Fact]
    public async Task HelloWithAStaleTokenReturnsUnauthorized()
    {
        var tempDir = NewTempDir();
        var tokens = TokenStore.CreateForTests(tempDir);
        var dispatcher = NewDispatcher(tokens);
        tokens.Rotate();
        var response = await dispatcher.HandleAsync(JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"hello","params":{"token":"wrong","protocolVersion":1,"clientName":"test"}}""").RootElement);
        Assert.Equal("UNAUTHORIZED", response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task StatusReportsALockedDesktop()
    {
        var tempDir = NewTempDir();
        var tokens = TokenStore.CreateForTests(tempDir);
        var dispatcher = NewDispatcher(tokens, new FakeDesktopState { Locked = true });
        var token = tokens.Rotate();

        // A real client authenticates first; per-connection gating lives in PipeServer.
        var hello = await dispatcher.HandleAsync(JsonDocument.Parse($$$"""{"jsonrpc":"2.0","id":1,"method":"hello","params":{"token":"{{{token}}}","protocolVersion":1,"clientName":"test"}}""").RootElement);
        Assert.True(hello.TryGetProperty("result", out _));

        var response = await dispatcher.HandleAsync(JsonDocument.Parse("""{"jsonrpc":"2.0","id":2,"method":"status","params":{}}""").RootElement);
        Assert.True(response.GetProperty("result").GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task WindowsListReturnsOpaqueIdsWithoutNativeHandles()
    {
        var tempDir = NewTempDir();
        var tokens = TokenStore.CreateForTests(tempDir);
        var api = FakeWindowApi.With(
            normal: new RawWindow(4242, 100, "Notepad", "Notepad", new Rect(10, 20, 800, 600)));
        var registry = new WindowRegistry(api);
        var enumerator = new WindowEnumerator(api, registry);
        var selection = new SelectionService(
            registry,
            new AllowAllAuthorization(),
            new SelectionStore(),
            windows: enumerator);
        var dispatcher = new JsonRpcDispatcher(
            tokens,
            new FakeMonitorProvider(),
            new FakeDesktopState(),
            enumerator,
            selection);
        var token = tokens.Rotate();

        var hello = await dispatcher.HandleAsync(JsonDocument.Parse($$$"""{"jsonrpc":"2.0","id":1,"method":"hello","params":{"token":"{{{token}}}","protocolVersion":1,"clientName":"test"}}""").RootElement);
        Assert.True(hello.TryGetProperty("result", out _));

        var response = await dispatcher.HandleAsync(JsonDocument.Parse("""{"jsonrpc":"2.0","id":2,"method":"windows.list","params":{}}""").RootElement);
        var windows = response.GetProperty("result").GetProperty("windows");
        var window = Assert.Single(windows.EnumerateArray());
        Assert.Equal("w_1", window.GetProperty("windowId").GetString());
        Assert.Equal("Notepad", window.GetProperty("title").GetString());
        Assert.Equal("Notepad", window.GetProperty("processName").GetString());
        Assert.Equal(100, window.GetProperty("pid").GetInt32());
        Assert.False(window.TryGetProperty("hwnd", out _));
        Assert.False(window.TryGetProperty("handle", out _));
    }

    [Fact]
    public async Task WindowSelectReturnsTheWindowAndStoresTheTarget()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(raw);
        var info = harness.Registry.Register(raw);

        var response = await harness.Dispatcher.HandleAsync(
            Request("window.select", $$"""{"windowId":"{{info.WindowId}}"}"""));

        var window = response.GetProperty("result").GetProperty("window");
        Assert.Equal(info.WindowId, window.GetProperty("windowId").GetString());
        Assert.Equal("Notepad", window.GetProperty("processName").GetString());
        Assert.True(window.GetProperty("focused").GetBoolean());
        Assert.Equal(1, harness.Focus.SetForegroundCalls);
    }

    [Fact]
    public async Task WindowSelectWithAmbiguousTitleReturnsInvalidRequestWithCandidates()
    {
        var harness = NewSelectionHarness(
            new RawWindow(1, 100, "a.txt - Notepad", "Notepad", new Rect(0, 0, 10, 10))
            {
                ClassName = "NotepadClass",
            },
            new RawWindow(2, 101, "b.txt - Notepad", "Notepad", new Rect(0, 0, 10, 10))
            {
                ClassName = "NotepadClass",
            });

        var response = await harness.Dispatcher.HandleAsync(
            Request("window.select", """{"title":"Notepad"}"""));

        var error = response.GetProperty("error");
        Assert.Equal("INVALID_REQUEST", error.GetProperty("data").GetProperty("code").GetString());
        var candidates = error.GetProperty("data").GetProperty("candidates");
        Assert.Equal(2, candidates.GetArrayLength());
        Assert.Equal(
            [100, 101],
            candidates.EnumerateArray().Select(candidate => candidate.GetProperty("pid").GetInt32()).Order());
        Assert.All(candidates.EnumerateArray(), candidate =>
        {
            Assert.StartsWith("w_", candidate.GetProperty("windowId").GetString());
            Assert.Contains("Notepad", candidate.GetProperty("title").GetString());
        });
        Assert.Equal(0, harness.Focus.SetForegroundCalls);
    }

    [Fact]
    public async Task WindowSelectWithoutATargetIsInvalidRequest()
    {
        var harness = NewSelectionHarness(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(Request("window.select", "{}"));

        Assert.Equal(
            "INVALID_REQUEST",
            response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ActionFocusBringsTheWindowToTheForeground()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(raw);
        var info = harness.Registry.Register(raw);

        var response = await harness.Dispatcher.HandleAsync(
            Request("action", $$"""{"kind":"focus","windowId":"{{info.WindowId}}"}"""));

        var window = response.GetProperty("result").GetProperty("window");
        Assert.Equal(info.WindowId, window.GetProperty("windowId").GetString());
        Assert.True(window.GetProperty("focused").GetBoolean());
        Assert.Equal(1, harness.Focus.SetForegroundCalls);
    }

    [Fact]
    public async Task ActionFocusOnADeadWindowReturnsWindowGone()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(raw);
        var info = harness.Registry.Register(raw);
        harness.Registry.MarkDeadForTests(raw.Hwnd);

        var response = await harness.Dispatcher.HandleAsync(
            Request("action", $$"""{"kind":"focus","windowId":"{{info.WindowId}}"}"""));

        Assert.Equal(
            "WINDOW_GONE",
            response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ActionFocusRequiresAWindowId()
    {
        var harness = NewSelectionHarness(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(Request("action", """{"kind":"focus"}"""));

        Assert.Equal(
            "INVALID_REQUEST",
            response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ActionRejectsUnsupportedKinds()
    {
        var harness = NewSelectionHarness(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(
            Request("action", """{"kind":"click","windowId":"w_1"}"""));

        Assert.Equal(
            "INVALID_REQUEST",
            response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    private static RawWindow NotepadRawWindow()
        => new(1, 100, "Notepad", "Notepad", new Rect(0, 0, 10, 10))
        {
            ClassName = "NotepadClass",
        };

    private static JsonElement Request(string method, string parametersJson)
        => JsonDocument.Parse(
            $$"""{"jsonrpc":"2.0","id":2,"method":"{{method}}","params":{{parametersJson}}}""").RootElement;

    private static SelectionHarness NewSelectionHarness(params RawWindow[] windows)
    {
        var tokens = TokenStore.CreateForTests(NewTempDir());
        tokens.Rotate();
        var api = new FakeWindowApi(windows);
        var registry = new WindowRegistry(api);
        var focus = new FakeWin32();
        var enumerator = new WindowEnumerator(api, registry);
        var selection = new SelectionService(
            registry,
            new AllowAllAuthorization(),
            new SelectionStore(),
            new ForegroundService(focus),
            enumerator,
            pid => $@"C:\tests\{pid}.exe");
        var dispatcher = new JsonRpcDispatcher(
            tokens,
            new FakeMonitorProvider(),
            new FakeDesktopState(),
            enumerator,
            selection);

        return new SelectionHarness(dispatcher, registry, focus);
    }

    private static JsonRpcDispatcher NewDispatcher(TokenStore tokens, FakeDesktopState? desktop = null)
    {
        var api = new FakeWindowApi();
        var registry = new WindowRegistry(api);
        var enumerator = new WindowEnumerator(api, registry);
        var selection = new SelectionService(
            registry,
            new AllowAllAuthorization(),
            new SelectionStore(),
            windows: enumerator,
            processPathResolver: pid => $@"C:\tests\{pid}.exe");
        return new(
            tokens,
            new FakeMonitorProvider(),
            desktop ?? new FakeDesktopState(),
            enumerator,
            selection);
    }

    private sealed record SelectionHarness(
        JsonRpcDispatcher Dispatcher,
        WindowRegistry Registry,
        FakeWin32 Focus);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kennect-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

internal sealed class FakeMonitorProvider : IMonitorProvider
{
    public IReadOnlyList<MonitorInfo> GetMonitors() =>
    [
        new MonitorInfo
        {
            Id = "mon_primary",
            Name = "Primary",
            Bounds = new Rect { X = 0, Y = 0, Width = 2560, Height = 1440 },
            Scale = 1.5,
            Primary = true,
        },
    ];
}

internal sealed class FakeDesktopState : IDesktopState
{
    public bool Locked { get; init; }

    public bool IsLocked() => Locked;
}
