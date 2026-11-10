using System.Text.Json;
using KennectComputer.Host.Auth;
using KennectComputer.Host.Ipc;
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
        var dispatcher = new JsonRpcDispatcher(
            tokens,
            new FakeMonitorProvider(),
            new FakeDesktopState(),
            new WindowEnumerator(api, new WindowRegistry()));
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

    private static JsonRpcDispatcher NewDispatcher(TokenStore tokens, FakeDesktopState? desktop = null)
        => new(
            tokens,
            new FakeMonitorProvider(),
            desktop ?? new FakeDesktopState(),
            new WindowEnumerator(new FakeWindowApi(), new WindowRegistry()));

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
