using System.Text.Json;
using KennectComputer.Host.Auth;
using KennectComputer.Host.Config;
using KennectComputer.Host.Control;
using KennectComputer.Host.Ipc;
using KennectComputer.Host.Policy;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
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
            selection,
            registry,
            NewSnapshotter(new FakeUiaWalker()),
            new SnapshotStore(),
            new ControlSession());
        var token = tokens.Rotate();

        var hello = await dispatcher.HandleAsync(JsonDocument.Parse($$$"""{"jsonrpc":"2.0","id":1,"method":"hello","params":{"token":"{{{token}}}","protocolVersion":1,"clientName":"test"}}""").RootElement);
        Assert.True(hello.TryGetProperty("result", out _));

        var response = await dispatcher.HandleAsync(JsonDocument.Parse("""{"jsonrpc":"2.0","id":2,"method":"windows.list","params":{}}""").RootElement);
        var windows = response.GetProperty("result").GetProperty("windows");
        var window = Assert.Single(windows.EnumerateArray());
        Assert.Matches("^w_[0-9]+$", window.GetProperty("windowId").GetString());
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

    [Fact]
    public async Task SnapshotReturnsTheAccessibilityOutline()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(raw);
        var info = harness.Registry.Register(raw);

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}"}"""));

        var result = response.GetProperty("result");
        Assert.Equal(info.WindowId, result.GetProperty("window").GetProperty("windowId").GetString());
        Assert.Equal(1, result.GetProperty("generation").GetInt32());
        Assert.False(result.GetProperty("modal").GetBoolean());
        Assert.False(result.TryGetProperty("image", out _));

        var nodes = result.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());
        Assert.True(result.GetProperty("totalNodes").GetInt32() >= nodes.GetArrayLength());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.StartsWith("n1:", nodes[0].GetProperty("nodeId").GetString());
        Assert.Equal(
            nodes[0].GetProperty("nodeId").GetString(),
            result.GetProperty("focusedNodeId").GetString());
        Assert.Equal(1, harness.Walker.Calls);
    }

    [Fact]
    public async Task SnapshotVisionModeSkipsTheAccessibilityTree()
    {
        var harness = NewSelectionHarness(NotepadRawWindow());
        var info = harness.Registry.Register(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}","mode":"vision"}"""));

        var result = response.GetProperty("result");
        Assert.Equal(0, harness.Walker.Calls);
        Assert.Equal(0, result.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, result.GetProperty("totalNodes").GetInt32());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.False(result.TryGetProperty("warning", out _));
    }

    [Fact]
    public async Task SnapshotRequiresAWindowId()
    {
        var harness = NewSelectionHarness(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(Request("snapshot", "{}"));

        Assert.Equal(
            "INVALID_REQUEST",
            response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.Equal(0, harness.Walker.Calls);
    }

    [Fact]
    public async Task SnapshotRejectsAnUnknownMode()
    {
        var harness = NewSelectionHarness(NotepadRawWindow());
        var info = harness.Registry.Register(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}","mode":"semantic"}"""));

        Assert.Equal(
            "INVALID_REQUEST",
            response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SnapshotOnADeadWindowReturnsWindowGone()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(raw);
        var info = harness.Registry.Register(raw);
        harness.Registry.MarkDeadForTests(raw.Hwnd);

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}"}"""));

        Assert.Equal(
            "WINDOW_GONE",
            response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.Equal(0, harness.Walker.Calls);
    }

    [Fact]
    public async Task SnapshotIncludesTheImageOnlyWhenRequested()
    {
        var payload = SampleImages.Payload();
        var capture = new FakeWindowCapture(payload);
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), capture, raw);
        var info = harness.Registry.Register(raw);

        var withoutImage = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}"}"""));

        Assert.False(withoutImage.GetProperty("result").TryGetProperty("image", out _));
        Assert.Empty(capture.Calls);

        var withImage = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}","image":true}"""));

        var image = withImage.GetProperty("result").GetProperty("image");
        Assert.Equal(payload.Data, image.GetProperty("data").GetString());
        Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
        Assert.Equal(1.5, image.GetProperty("scale").GetDouble());
        Assert.Equal(800, image.GetProperty("sourceBounds").GetProperty("width").GetInt32());

        // The dispatcher must capture the resolved window's authoritative bounds.
        var call = Assert.Single(capture.Calls);
        Assert.Equal(raw.Hwnd, call.Hwnd);
        Assert.Equal(raw.Bounds.X, call.Bounds.X);
        Assert.Equal(raw.Bounds.Y, call.Bounds.Y);
        Assert.Equal(raw.Bounds.Width, call.Bounds.Width);
        Assert.Equal(raw.Bounds.Height, call.Bounds.Height);
        Assert.True(call.Enabled);
    }

    [Fact]
    public async Task SnapshotVisionModeStillCapturesTheImage()
    {
        var payload = SampleImages.Payload();
        var capture = new FakeWindowCapture(payload);
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(alwaysEmpty: true), capture, raw);
        var info = harness.Registry.Register(raw);

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}","mode":"vision","image":true}"""));

        var result = response.GetProperty("result");
        Assert.Equal(0, result.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, harness.Walker.Calls);
        Assert.Equal(payload.Data, result.GetProperty("image").GetProperty("data").GetString());
    }

    [Fact]
    public async Task SnapshotSurvivesAFailedImageCapture()
    {
        var capture = new FakeWindowCapture(payload: null);
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), capture, raw);
        var info = harness.Registry.Register(raw);

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}","image":true}"""));

        var result = response.GetProperty("result");
        Assert.False(result.TryGetProperty("image", out _));
        Assert.False(result.TryGetProperty("warning", out _));
        Assert.Equal(2, result.GetProperty("nodes").GetArrayLength());
    }

    [Fact]
    public async Task SnapshotAppliesTheNodeCap()
    {
        var harness = NewSelectionHarness(NotepadRawWindow());
        var info = harness.Registry.Register(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}","maxNodes":1}"""));

        var result = response.GetProperty("result");
        Assert.Equal(1, result.GetProperty("nodes").GetArrayLength());
        Assert.True(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task SnapshotReturnsTheWarningWhenTheTreeStaysEmpty()
    {
        var harness = NewSelectionHarness(
            new FakeUiaWalker(alwaysEmpty: true),
            NotepadRawWindow());
        var info = harness.Registry.Register(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}","mode":"a11y"}"""));

        var result = response.GetProperty("result");
        Assert.Equal(
            "accessibility tree empty; vision fallback required",
            result.GetProperty("warning").GetString());
        Assert.Equal(0, result.GetProperty("nodes").GetArrayLength());
        Assert.Equal(4, harness.Walker.Calls);
    }

    [Fact]
    public async Task SnapshotRejectsOutOfRangeDepthAndNodeLimits()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(raw);
        var info = harness.Registry.Register(raw);

        foreach (var parameters in new[]
        {
            $$"""{"windowId":"{{info.WindowId}}","maxDepth":0}""",
            $$"""{"windowId":"{{info.WindowId}}","maxDepth":-1}""",
            $$"""{"windowId":"{{info.WindowId}}","maxDepth":101}""",
            $$"""{"windowId":"{{info.WindowId}}","maxNodes":0}""",
            $$"""{"windowId":"{{info.WindowId}}","maxNodes":-5}""",
            $$"""{"windowId":"{{info.WindowId}}","maxNodes":50001}""",
        })
        {
            var response = await harness.Dispatcher.HandleAsync(
                Request("snapshot", parameters));

            Assert.Equal(
                "INVALID_REQUEST",
                response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        }

        Assert.Equal(0, harness.Walker.Calls);
    }

    [Fact]
    public async Task SnapshotReturnsWindowGoneWhenTheWindowDiesDuringTheWalk()
    {
        var raw = NotepadRawWindow();
        var api = new FakeWindowApi(raw);
        var walker = new FakeUiaWalker(onWalk: hwnd => api.MarkDead(hwnd));
        var harness = NewSelectionHarness(
            walker,
            new FakeWindowCapture(),
            exclusions: null,
            perception: null,
            desktop: null,
            api: api);
        var info = harness.Registry.Register(raw);

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}"}"""));

        Assert.Equal(
            "WINDOW_GONE",
            response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.Equal(1, harness.Walker.Calls);

        // The dead ID is removed, so a retry is WINDOW_GONE without walking again.
        var retry = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}"}"""));
        Assert.Equal(
            "WINDOW_GONE",
            retry.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.Equal(1, harness.Walker.Calls);
    }

    [Fact]
    public async Task SnapshotOnAWindowThatBecameExcludedReturnsWindowGoneAndClearsSelection()
    {
        // The ID outlives the title: a Chrome window enumerated as "New Tab"
        // navigates to banking after the ID was issued.
        var raw = NotepadRawWindow();
        var api = new FakeWindowApi(raw);
        var policy = new ExclusionPolicy(
            new AppConfig
            {
                Privacy = new PrivacyConfig { ExcludedTitles = ["*bank*"] },
            });
        var harness = NewSelectionHarness(
            new FakeUiaWalker(),
            new FakeWindowCapture(),
            exclusions: policy,
            perception: null,
            desktop: null,
            api: api);
        var info = harness.Registry.Register(raw);

        var selected = await harness.Dispatcher.HandleAsync(
            Request("window.select", $$"""{"windowId":"{{info.WindowId}}"}"""));
        Assert.True(selected.TryGetProperty("result", out _));
        Assert.NotNull(harness.Selection.Selected);

        api.Replace(raw with { Title = "Bank of America - Online Banking" });

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}"}"""));

        Assert.Equal(
            "WINDOW_GONE",
            response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.Equal(0, harness.Walker.Calls);
        Assert.Null(harness.Selection.Selected);
    }

    [Fact]
    public async Task SelectingAWindowThatBecameExcludedReturnsWindowGone()
    {
        var raw = NotepadRawWindow();
        var api = new FakeWindowApi(raw);
        var policy = new ExclusionPolicy(
            new AppConfig
            {
                Privacy = new PrivacyConfig { ExcludedTitles = ["*bank*"] },
            });
        var harness = NewSelectionHarness(
            new FakeUiaWalker(),
            new FakeWindowCapture(),
            exclusions: policy,
            perception: null,
            desktop: null,
            api: api);
        var info = harness.Registry.Register(raw);

        api.Replace(raw with { Title = "Bank of America - Online Banking" });

        var response = await harness.Dispatcher.HandleAsync(
            Request("window.select", $$"""{"windowId":"{{info.WindowId}}"}"""));

        Assert.Equal(
            "WINDOW_GONE",
            response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.Equal(0, harness.Focus.SetForegroundCalls);
        Assert.Null(harness.Selection.Selected);
    }

    [Fact]
    public async Task SnapshotUsesTheConfiguredPerceptionDefaults()
    {
        var capture = new FakeWindowCapture(SampleImages.Payload());
        var harness = NewSelectionHarness(
            new FakeUiaWalker(),
            capture,
            exclusions: null,
            perception: new PerceptionConfig
            {
                MaxDepth = 3,
                MaxNodes = 7,
                ImageLongEdge = 640,
                MaxImageLongEdge = 800,
            },
            desktop: null,
            api: null,
            windows: [NotepadRawWindow()]);
        var info = harness.Registry.Register(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(
            Request(
                "snapshot",
                $$"""{"windowId":"{{info.WindowId}}","image":true}"""));

        Assert.True(response.TryGetProperty("result", out _));
        Assert.Equal(3, harness.Walker.LastOptions?.MaxDepth);
        Assert.Equal(7, harness.Walker.LastOptions?.MaxNodes);
        var call = Assert.Single(capture.Calls);
        Assert.Equal(640, call.ImageLongEdge);
        Assert.Equal(800, call.MaxImageLongEdge);
    }

    [Fact]
    public async Task SnapshotUsesTheConfiguredDefaultMode()
    {
        var harness = NewSelectionHarness(
            new FakeUiaWalker(),
            new FakeWindowCapture(),
            exclusions: null,
            perception: new PerceptionConfig { DefaultMode = "vision" },
            desktop: null,
            api: null,
            windows: [NotepadRawWindow()]);
        var info = harness.Registry.Register(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}"}"""));

        var result = response.GetProperty("result");
        Assert.Equal(0, result.GetProperty("nodes").GetArrayLength());
        Assert.Equal(0, harness.Walker.Calls);
    }

    [Fact]
    public async Task ALockedDesktopFailsSnapshotAndSelectClosed()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(
            new FakeUiaWalker(),
            new FakeWindowCapture(),
            exclusions: null,
            perception: null,
            desktop: new FakeDesktopState { Locked = true },
            api: null,
            windows: [raw]);
        var info = harness.Registry.Register(raw);

        foreach (var request in new[]
        {
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}"}"""),
            Request("window.select", $$"""{"windowId":"{{info.WindowId}}"}"""),
            Request("action", $$"""{"kind":"focus","windowId":"{{info.WindowId}}"}"""),
        })
        {
            var response = await harness.Dispatcher.HandleAsync(request);

            Assert.Equal(
                "DESKTOP_UNAVAILABLE",
                response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        }

        Assert.Equal(0, harness.Walker.Calls);
        Assert.Equal(0, harness.Focus.SetForegroundCalls);
    }

    [Fact]
    public async Task StatusStillReportsALockedDesktop()
    {
        var tokens = TokenStore.CreateForTests(NewTempDir());
        var dispatcher = NewDispatcher(tokens, new FakeDesktopState { Locked = true });

        var response = await dispatcher.HandleAsync(Request("status", "{}"));

        Assert.True(response.GetProperty("result").GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task ASecondConnectionIsLockedOutUntilTheFirstReleases()
    {
        var tokens = TokenStore.CreateForTests(NewTempDir());
        var control = new ControlSession();
        var dispatcher = NewDispatcher(tokens, control: control);
        var token = tokens.Rotate();

        var first = await dispatcher.HandleAsync(HelloRequest(token), "connection-1");
        Assert.True(first.TryGetProperty("result", out _));
        Assert.Equal("connection-1", control.Owner);

        var locked = await dispatcher.HandleAsync(HelloRequest(token), "connection-2");
        Assert.Equal(
            "LOCKED_BY_OTHER_SESSION",
            locked.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.Equal("connection-1", control.Owner);

        dispatcher.ReleaseSession("connection-1");
        var second = await dispatcher.HandleAsync(HelloRequest(token), "connection-2");
        Assert.True(second.TryGetProperty("result", out _));
        Assert.Equal("connection-2", control.Owner);
    }

    [Fact]
    public async Task RepeatingHelloOnTheSameConnectionIsIdempotent()
    {
        var tokens = TokenStore.CreateForTests(NewTempDir());
        var control = new ControlSession();
        var dispatcher = NewDispatcher(tokens, control: control);
        var token = tokens.Rotate();

        Assert.True((await dispatcher.HandleAsync(HelloRequest(token), "connection-1"))
            .TryGetProperty("result", out _));
        Assert.True((await dispatcher.HandleAsync(HelloRequest(token), "connection-1"))
            .TryGetProperty("result", out _));

        Assert.Equal("connection-1", control.Owner);
        Assert.False(control.TryAcquire("connection-2"));
    }

    [Fact]
    public async Task AStaleTokenDoesNotAcquireTheControlLock()
    {
        var tokens = TokenStore.CreateForTests(NewTempDir());
        var control = new ControlSession();
        var dispatcher = NewDispatcher(tokens, control: control);
        var token = tokens.Rotate();

        var rejected = await dispatcher.HandleAsync(HelloRequest("stale-token"), "connection-1");
        Assert.Equal(
            "UNAUTHORIZED",
            rejected.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.False(control.IsHeld);

        var accepted = await dispatcher.HandleAsync(HelloRequest(token), "connection-2");
        Assert.True(accepted.TryGetProperty("result", out _));
        Assert.Equal("connection-2", control.Owner);
    }

    [Fact]
    public async Task StopReturnsOkAndCancelsTheCurrentOperation()
    {
        var tokens = TokenStore.CreateForTests(NewTempDir());
        var control = new ControlSession();
        var dispatcher = NewDispatcher(tokens, control: control);
        var token = tokens.Rotate();

        Assert.True((await dispatcher.HandleAsync(HelloRequest(token), "connection-1"))
            .TryGetProperty("result", out _));
        var operation = control.Token;

        var response = await dispatcher.HandleAsync(Request("stop", "{}"), "connection-1");

        Assert.True(response.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.True(operation.IsCancellationRequested);
        // Stop aborts the operation, not the session (R35).
        Assert.True(control.IsHeld);
        Assert.False(control.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task StopWithNoControlSessionStillSucceeds()
    {
        var tokens = TokenStore.CreateForTests(NewTempDir());
        var control = new ControlSession();
        var dispatcher = NewDispatcher(tokens, control: control);

        var response = await dispatcher.HandleAsync(Request("stop", "{}"), "connection-1");

        Assert.True(response.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.False(control.IsHeld);
    }

    private static JsonElement HelloRequest(string token)
        => JsonDocument.Parse(
            $$$"""{"jsonrpc":"2.0","id":1,"method":"hello","params":{"token":"{{{token}}}","protocolVersion":1,"clientName":"test"}}""")
            .RootElement;

    private static RawWindow NotepadRawWindow()
        => new(1, 100, "Notepad", "Notepad", new Rect(0, 0, 10, 10))
        {
            ClassName = "NotepadClass",
        };

    private static JsonElement Request(string method, string parametersJson)
        => JsonDocument.Parse(
            $$"""{"jsonrpc":"2.0","id":2,"method":"{{method}}","params":{{parametersJson}}}""").RootElement;

    private static SelectionHarness NewSelectionHarness(params RawWindow[] windows)
        => NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), windows);

    private static SelectionHarness NewSelectionHarness(IUiaWalker walker, params RawWindow[] windows)
        => NewSelectionHarness(walker, new FakeWindowCapture(), windows);

    private static SelectionHarness NewSelectionHarness(
        IUiaWalker walker,
        FakeWindowCapture capture,
        params RawWindow[] windows)
        => NewSelectionHarness(
            walker,
            capture,
            exclusions: null,
            perception: null,
            desktop: null,
            api: null,
            windows: windows);

    private static SelectionHarness NewSelectionHarness(
        IUiaWalker walker,
        FakeWindowCapture capture,
        IExclusionPolicy? exclusions,
        PerceptionConfig? perception,
        FakeDesktopState? desktop,
        FakeWindowApi? api,
        params RawWindow[] windows)
    {
        var tokens = TokenStore.CreateForTests(NewTempDir());
        tokens.Rotate();
        api ??= new FakeWindowApi(windows);
        var registry = new WindowRegistry(api, exclusions);
        var focus = new FakeWin32();
        var enumerator = new WindowEnumerator(api, registry, exclusions);
        var selectionStore = new SelectionStore();
        var selection = new SelectionService(
            registry,
            new AllowAllAuthorization(),
            selectionStore,
            new ForegroundService(focus),
            enumerator,
            pid => $@"C:\tests\{pid}.exe");
        var fakeWalker = walker as FakeUiaWalker ?? new FakeUiaWalker();
        var dispatcher = new JsonRpcDispatcher(
            tokens,
            new FakeMonitorProvider(),
            desktop ?? new FakeDesktopState(),
            enumerator,
            selection,
            registry,
            NewSnapshotter(walker, capture),
            new SnapshotStore(),
            new ControlSession(),
            perception);

        return new SelectionHarness(
            dispatcher,
            registry,
            focus,
            fakeWalker,
            capture,
            api,
            selectionStore);
    }

    private static JsonRpcDispatcher NewDispatcher(
        TokenStore tokens,
        FakeDesktopState? desktop = null,
        ControlSession? control = null)
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
            selection,
            registry,
            NewSnapshotter(new FakeUiaWalker()),
            new SnapshotStore(),
            control ?? new ControlSession());
    }

    private static UiaSnapshotter NewSnapshotter(IUiaWalker walker, FakeWindowCapture? capture = null)
    {
        WindowCapture? captureImage = capture is null ? null : capture.Capture;
        return new(walker, _ => { }, _ => { }, captureImage);
    }

    private sealed record SelectionHarness(
        JsonRpcDispatcher Dispatcher,
        WindowRegistry Registry,
        FakeWin32 Focus,
        FakeUiaWalker Walker,
        FakeWindowCapture Capture,
        FakeWindowApi Api,
        SelectionStore Selection);

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
