using System.Text.Json;
using KennectComputer.Host.Actions;
using KennectComputer.Host.Auth;
using KennectComputer.Host.Config;
using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
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
        var selectionStore = new SelectionStore();
        var selection = new SelectionService(
            registry,
            new AllowAllAuthorization(),
            selectionStore,
            windows: enumerator);
        var walker = new FakeUiaWalker();
        var focus = new FakeWin32();
        var snapshots = new SnapshotStore();
        var snapshotter = NewSnapshotter(walker);
        var actions = NewActions(
            walker,
            focus,
            registry,
            enumerator,
            selectionStore,
            snapshots,
            snapshotter,
            new FakeInputSender(),
            new FakeClipboard(),
            new DispatcherWaitClock(),
            liveNode: null);
        var dispatcher = new JsonRpcDispatcher(
            tokens,
            new FakeMonitorProvider(),
            new FakeDesktopState(),
            enumerator,
            selection,
            registry,
            snapshotter,
            snapshots,
            new ControlSession(),
            actions.Click,
            actions.Type,
            actions.Key,
            actions.Scroll,
            actions.Wait,
            actions.ReadText,
            actions.Find,
            actions.Batch);
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
            Request("action", """{"kind":"teleport","windowId":"w_1"}"""));

        Assert.Equal("INVALID_REQUEST", ErrorCodeOf(response));
    }

    [Fact]
    public async Task ActionClickWithANodeCallsTheClickServiceAndReturnsTheActionResult()
    {
        var raw = NotepadRawWindow();
        var target = ButtonTarget();
        var harness = NewSelectionHarness(
            new FakeUiaWalker(),
            new FakeWindowCapture(),
            exclusions: null,
            perception: null,
            desktop: null,
            api: null,
            liveNode: FakeUiaNode.From(target),
            windows: [raw]);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);
        harness.SaveNode("n1:1", target);

        var response = await harness.Dispatcher.HandleAsync(
            Request("action", """{"kind":"click","nodeId":"n1:1"}"""));

        var result = response.GetProperty("result");
        Assert.Equal("click", result.GetProperty("action").GetString());
        Assert.Equal("pattern", result.GetProperty("method").GetString());
        Assert.True(result.GetProperty("needsObservation").GetBoolean());
        Assert.Equal(info.WindowId, result.GetProperty("window").GetProperty("windowId").GetString());

        // The click was semantic: it focused the window and sent no mouse input.
        Assert.Equal(1, harness.Focus.SetForegroundCalls);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ActionClickWithoutASelectedWindowReturnsInvalidRequest()
    {
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(
            Request("action", """{"kind":"click","x":5,"y":5,"space":"screen"}"""));

        Assert.Equal("INVALID_REQUEST", ErrorCodeOf(response));
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Focus.SetForegroundCalls);
    }

    [Fact]
    public async Task ActionClickWithAStaleNodeReturnsStaleSnapshot()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), raw);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);
        harness.SaveNode("n1:1", ButtonTarget());

        var response = await harness.Dispatcher.HandleAsync(
            Request("action", """{"kind":"click","nodeId":"n2:1"}"""));

        Assert.Equal("STALE_SNAPSHOT", ErrorCodeOf(response));
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Focus.SetForegroundCalls);
    }

    [Fact]
    public async Task ActionClickAcceptsANullExpectAsNoPredicates()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), raw);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);

        var response = await harness.Dispatcher.HandleAsync(
            Request(
                "action",
                """{"kind":"click","x":5,"y":5,"space":"screen","expect":null}"""));

        Assert.True(response.TryGetProperty("result", out _));
        Assert.Equal([(5, 5)], harness.Input.Moves);
    }

    [Fact]
    public async Task ActionClickRejectsACountAboveTenBeforeInput()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), raw);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);

        var response = await harness.Dispatcher.HandleAsync(
            Request(
                "action",
                """{"kind":"click","x":5,"y":5,"space":"screen","count":11}"""));

        Assert.Equal("INVALID_REQUEST", ErrorCodeOf(response));
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ActionTypeRejectsTextAboveOneHundredThousandCharacters()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), raw);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);
        var text = new string('a', 100_001);

        var response = await harness.Dispatcher.HandleAsync(
            Request("action", $$"""{"kind":"type","text":"{{text}}"}"""));

        Assert.Equal("INVALID_REQUEST", ErrorCodeOf(response));
        Assert.Empty(harness.Clipboard.Sets);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ActionTypePastesTextAndReturnsTheActionResult()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), raw);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);

        var response = await harness.Dispatcher.HandleAsync(
            Request("action", """{"kind":"type","text":"hello"}"""));

        var result = response.GetProperty("result");
        Assert.Equal("type", result.GetProperty("action").GetString());
        Assert.Equal("paste", result.GetProperty("method").GetString());

        // Paste sets the text and then restores the (empty) clipboard.
        Assert.Equal(["hello", string.Empty], harness.Clipboard.Sets);
        Assert.Equal(
            ["KeyDown:17", "KeyDown:86", "KeyUp:86", "KeyUp:17"],
            harness.Input.Calls);
    }

    [Fact]
    public async Task ActionKeySendsTheChordAndReturnsTheActionResult()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), raw);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);

        var response = await harness.Dispatcher.HandleAsync(
            Request("action", """{"kind":"key","keys":"Enter"}"""));

        var result = response.GetProperty("result");
        Assert.Equal("key", result.GetProperty("action").GetString());
        Assert.Equal("keys", result.GetProperty("method").GetString());
        Assert.Equal(["KeyDown:13", "KeyUp:13"], harness.Input.Calls);
    }

    [Fact]
    public async Task ActionScrollAtACoordinateTurnsTheWheel()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), raw);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);

        var response = await harness.Dispatcher.HandleAsync(
            Request(
                "action",
                """{"kind":"scroll","x":5,"y":5,"space":"screen","direction":"down","amount":2}"""));

        var result = response.GetProperty("result");
        Assert.Equal("scroll", result.GetProperty("action").GetString());
        Assert.Equal("wheel", result.GetProperty("method").GetString());
        Assert.Equal([(5, 5)], harness.Input.Moves);
        Assert.Equal([(-240, false)], harness.Input.Scrolls);
    }

    [Fact]
    public async Task ActionClickReturnsFocusRefusedAndSendsNoInput()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(
            new FakeUiaWalker(),
            new FakeWindowCapture(),
            exclusions: null,
            perception: null,
            desktop: null,
            api: null,
            foregroundSucceeds: false,
            windows: [raw]);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);

        var response = await harness.Dispatcher.HandleAsync(
            Request("action", """{"kind":"click","x":5,"y":5,"space":"screen"}"""));

        Assert.Equal("FOCUS_REFUSED", ErrorCodeOf(response));
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ActionClickWithAFailedExpectationReturnsExpectationFailedWithThePredicate()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), raw);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);

        var response = await harness.Dispatcher.HandleAsync(
            Request(
                "action",
                """{"kind":"click","x":5,"y":5,"space":"screen","expect":[{"kind":"node","name":"Missing"}]}"""));

        Assert.Equal("EXPECTATION_FAILED", ErrorCodeOf(response));
        Assert.Contains(
            "node:Missing",
            response.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Focus.SetForegroundCalls);
    }

    [Fact]
    public async Task ActionRejectsUnknownFields()
    {
        var harness = NewSelectionHarness(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(
            Request("action", """{"kind":"click","x":1,"y":2,"space":"screen","bogus":true}"""));

        Assert.Equal("INVALID_REQUEST", ErrorCodeOf(response));
    }

    [Theory]
    [InlineData("action", """{"kind":"scroll","x":1,"y":2,"space":"screen"}""")]
    [InlineData("action", """{"kind":"key"}""")]
    [InlineData("action", """{"kind":"type"}""")]
    [InlineData("action", """{"kind":"click","nodeId":"n1:1","x":1,"y":2}""")]
    [InlineData("find", """{"maxResults":0}""")]
    [InlineData("wait", """{"condition":{"kind":"text"}}""")]
    [InlineData("read_text", """{}""")]
    [InlineData("action.batch", """{}""")]
    [InlineData("action.batch", """{"steps":[]}""")]
    [InlineData("action.batch", """{"steps":[{"kind":"teleport"}]}""")]
    public async Task MissingOrInvalidRequiredFieldsAreRejected(string method, string parametersJson)
    {
        var harness = NewSelectionHarness(NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(Request(method, parametersJson));

        Assert.Equal("INVALID_REQUEST", ErrorCodeOf(response));
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task FindReturnsMatchesWithAGeneration()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), raw);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);

        var response = await harness.Dispatcher.HandleAsync(
            Request("find", """{"text":"send"}"""));

        var result = response.GetProperty("result");
        var generation = result.GetProperty("generation").GetInt32();
        Assert.True(generation > 0);

        var match = Assert.Single(result.GetProperty("matches").EnumerateArray());
        Assert.Equal("button", match.GetProperty("role").GetString());
        Assert.Equal("Send", match.GetProperty("name").GetString());
        Assert.StartsWith($"n{generation}:", match.GetProperty("nodeId").GetString());
        Assert.Equal(info.WindowId, result.GetProperty("window").GetProperty("windowId").GetString());
    }

    [Fact]
    public async Task ReadTextReturnsTheText()
    {
        var raw = NotepadRawWindow();
        var target = EditTarget();
        var live = FakeUiaNode.From(target) with { Text = "Hello world" };
        var harness = NewSelectionHarness(
            new FakeUiaWalker(),
            new FakeWindowCapture(),
            exclusions: null,
            perception: null,
            desktop: null,
            api: null,
            liveNode: live,
            windows: [raw]);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);
        harness.SaveNode("n1:4", target);

        var response = await harness.Dispatcher.HandleAsync(
            Request("read_text", """{"nodeId":"n1:4"}"""));

        var result = response.GetProperty("result");
        Assert.Equal("n1:4", result.GetProperty("nodeId").GetString());
        Assert.Equal("Hello world", result.GetProperty("text").GetString());
        Assert.Equal(11, result.GetProperty("length").GetInt32());
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task WaitPassesTheConditionAndReturnsTheMatchingWindow()
    {
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), raw);

        var response = await harness.Dispatcher.HandleAsync(
            Request(
                "wait",
                """{"condition":{"kind":"window","process":"Notepad"},"present":true,"timeoutMs":5000,"pollMs":100}"""));

        var result = response.GetProperty("result");
        Assert.Equal(100, result.GetProperty("window").GetProperty("pid").GetInt32());
        var condition = result.GetProperty("condition");
        Assert.Equal("window", condition.GetProperty("kind").GetString());
        Assert.Equal("Notepad", condition.GetProperty("process").GetString());
        Assert.Equal(0L, result.GetProperty("elapsedMs").GetInt64());

        // A window condition needs no selection and never walks a tree.
        Assert.Equal(0, harness.Walker.Calls);
        Assert.Empty(harness.Clock.Delays);
    }

    [Fact]
    public async Task WaitTimeoutMapsToActionTimeoutNamingTheCondition()
    {
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(
            Request("wait", """{"condition":{"kind":"window","process":"Missing"},"timeoutMs":5}"""));

        Assert.Equal("ACTION_TIMEOUT", ErrorCodeOf(response));
        Assert.Contains(
            "5 ms",
            response.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ActionBatchReturnsPerStepResultsAndTheFailedIndex()
    {
        var raw = NotepadRawWindow();
        var target = EditTarget();
        var live = FakeUiaNode.From(target) with { Text = "Batch text" };
        var harness = NewSelectionHarness(
            new FakeUiaWalker(),
            new FakeWindowCapture(),
            exclusions: null,
            perception: null,
            desktop: null,
            api: null,
            liveNode: live,
            windows: [raw]);
        var info = harness.Registry.Register(raw);
        harness.Selection.Select(info);
        harness.SaveNode("n1:4", target);

        var response = await harness.Dispatcher.HandleAsync(
            Request(
                "action.batch",
                """
                {"steps":[
                  {"kind":"key","keys":"Enter"},
                  {"kind":"readText","nodeId":"n1:4"},
                  {"kind":"click","nodeId":"n2:4"},
                  {"kind":"key","keys":"Tab"}
                ]}
                """));

        var result = response.GetProperty("result");
        Assert.Equal(2, result.GetProperty("failedIndex").GetInt32());

        var steps = result.GetProperty("steps");
        Assert.Equal(3, steps.GetArrayLength());

        Assert.Equal(0, steps[0].GetProperty("index").GetInt32());
        Assert.Equal("key", steps[0].GetProperty("kind").GetString());
        Assert.True(steps[0].GetProperty("ok").GetBoolean());
        Assert.Equal("key", steps[0].GetProperty("data").GetProperty("action").GetString());

        Assert.Equal(1, steps[1].GetProperty("index").GetInt32());
        Assert.Equal("readText", steps[1].GetProperty("kind").GetString());
        Assert.True(steps[1].GetProperty("ok").GetBoolean());
        Assert.Equal("Batch text", steps[1].GetProperty("data").GetProperty("text").GetString());

        Assert.Equal(2, steps[2].GetProperty("index").GetInt32());
        Assert.False(steps[2].GetProperty("ok").GetBoolean());
        Assert.Equal("STALE_SNAPSHOT", steps[2].GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(steps[2].GetProperty("message").GetString()));
        Assert.False(steps[2].TryGetProperty("data", out _));

        // The fourth step never ran.
        Assert.Equal(["KeyDown:13", "KeyUp:13"], harness.Input.Calls);
    }

    [Fact]
    public async Task ActionBatchIncludesWaitStepDataWithItsCondition()
    {
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), NotepadRawWindow());

        var response = await harness.Dispatcher.HandleAsync(
            Request(
                "action.batch",
                """{"steps":[{"kind":"wait","condition":{"kind":"window","process":"Notepad"},"timeoutMs":1000}]}"""));

        var step = Assert.Single(
            response.GetProperty("result").GetProperty("steps").EnumerateArray());
        Assert.True(step.GetProperty("ok").GetBoolean());

        var data = step.GetProperty("data");
        Assert.Equal("window", data.GetProperty("condition").GetProperty("kind").GetString());
        Assert.Equal("Notepad", data.GetProperty("condition").GetProperty("process").GetString());
        Assert.Equal(100, data.GetProperty("window").GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task AStoppedWaitReturnsActionTimeoutWithTheStopMessage()
    {
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), NotepadRawWindow());
        harness.Clock.OnDelay = _ => harness.Control.Stop();

        var response = await harness.Dispatcher.HandleAsync(
            Request("wait", """{"condition":{"kind":"window","process":"Missing"},"timeoutMs":60000}"""));

        Assert.Equal("ACTION_TIMEOUT", ErrorCodeOf(response));
        Assert.Equal(
            "The operation was stopped.",
            response.GetProperty("error").GetProperty("message").GetString());
        Assert.Single(harness.Clock.Delays);
    }

    [Fact]
    public async Task ABatchStoppedInsideAStepReturnsActionTimeoutWithoutPartialResults()
    {
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(), NotepadRawWindow());
        harness.Clock.OnDelay = _ => harness.Control.Stop();

        var response = await harness.Dispatcher.HandleAsync(
            Request(
                "action.batch",
                """{"steps":[{"kind":"wait","condition":{"kind":"window","process":"Missing"},"timeoutMs":60000}]}"""));

        Assert.Equal("ACTION_TIMEOUT", ErrorCodeOf(response));
        Assert.Equal(
            "The operation was stopped.",
            response.GetProperty("error").GetProperty("message").GetString());
        Assert.False(response.TryGetProperty("result", out _));
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
    public async Task SnapshotSavesTheReResolvedWindowBoundsForActions()
    {
        var payload = SampleImages.Payload();
        var raw = NotepadRawWindow();
        var harness = NewSelectionHarness(new FakeUiaWalker(), new FakeWindowCapture(payload), raw);
        var info = harness.Registry.Register(raw);

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}","image":true}"""));

        Assert.True(response.TryGetProperty("result", out _));
        Assert.True(harness.Snapshots.TryGetState(info.WindowId, out var state));
        Assert.NotNull(state);
        Assert.Equal(1, state.Generation);
        Assert.Equal(raw.Bounds.X, state.WindowBounds.X);
        Assert.Equal(raw.Bounds.Y, state.WindowBounds.Y);
        Assert.Equal(raw.Bounds.Width, state.WindowBounds.Width);
        Assert.Equal(raw.Bounds.Height, state.WindowBounds.Height);
        Assert.NotNull(state.Image);
        Assert.Equal(payload.Scale, state.Image.Scale);
    }

    [Fact]
    public async Task SnapshotSavesTheBoundsTheImageWasCapturedFrom()
    {
        // The window moves while the tree is being walked. The image was
        // captured against the pre-walk bounds, so saving the post-walk bounds
        // would make the live and saved bounds agree while the image still
        // maps against the old source rectangle.
        var payload = SampleImages.Payload();
        var raw = NotepadRawWindow();
        var moved = raw with { Bounds = new Rect(50, 60, 800, 600) };
        var api = new FakeWindowApi(raw);
        var walker = new FakeUiaWalker(onWalk: _ => api.Replace(moved));
        var harness = NewSelectionHarness(
            walker,
            new FakeWindowCapture(payload),
            exclusions: null,
            perception: null,
            desktop: null,
            api: api);
        var info = harness.Registry.Register(raw);

        var response = await harness.Dispatcher.HandleAsync(
            Request("snapshot", $$"""{"windowId":"{{info.WindowId}}","image":true}"""));

        Assert.True(response.TryGetProperty("result", out _));
        Assert.True(harness.Snapshots.TryGetState(info.WindowId, out var state));
        Assert.NotNull(state?.Image);

        // The saved bounds are the ones the capture used, not where the window
        // ended up, so an image-space action detects the move.
        Assert.Equal(raw.Bounds.X, state!.WindowBounds.X);
        Assert.Equal(raw.Bounds.Y, state.WindowBounds.Y);
        Assert.Equal(raw.Bounds.Width, state.WindowBounds.Width);
        Assert.Equal(raw.Bounds.Height, state.WindowBounds.Height);

        harness.Selection.Select(info);
        var click = await harness.Dispatcher.HandleAsync(
            Request("action", """{"kind":"click","x":1,"y":1}"""));

        Assert.Equal("STALE_SNAPSHOT", ErrorCodeOf(click));
        Assert.Empty(harness.Input.Calls);
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

    private static string? ErrorCodeOf(JsonElement response)
        => response.GetProperty("error").GetProperty("data").GetProperty("code").GetString();

    private static TreeModel ButtonTarget() => new()
    {
        Role = "button",
        Name = "Send",
        Bounds = new Rect(10, 20, 120, 32),
        Actions = ["press"],
    };

    private static TreeModel EditTarget() => new()
    {
        Role = "edit",
        Name = "Message",
        Bounds = new Rect(10, 20, 200, 40),
        Actions = ["setValue"],
    };

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
        IUiaNode? liveNode = null,
        bool foregroundSucceeds = true,
        params RawWindow[] windows)
    {
        var tokens = TokenStore.CreateForTests(NewTempDir());
        tokens.Rotate();
        api ??= new FakeWindowApi(windows);
        var registry = new WindowRegistry(api, exclusions);
        var focus = new FakeWin32
        {
            InitialForeground = 99,
            ForegroundSucceeds = foregroundSucceeds,
        };
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
        var snapshots = new SnapshotStore();
        var snapshotter = NewSnapshotter(walker, capture);
        var input = new FakeInputSender();
        var clipboard = new FakeClipboard();
        var clock = new DispatcherWaitClock();
        var control = new ControlSession();
        var actions = NewActions(
            walker,
            focus,
            registry,
            enumerator,
            selectionStore,
            snapshots,
            snapshotter,
            input,
            clipboard,
            clock,
            liveNode);
        var dispatcher = new JsonRpcDispatcher(
            tokens,
            new FakeMonitorProvider(),
            desktop ?? new FakeDesktopState(),
            enumerator,
            selection,
            registry,
            snapshotter,
            snapshots,
            control,
            actions.Click,
            actions.Type,
            actions.Key,
            actions.Scroll,
            actions.Wait,
            actions.ReadText,
            actions.Find,
            actions.Batch,
            perception);

        return new SelectionHarness(
            dispatcher,
            registry,
            focus,
            fakeWalker,
            capture,
            api,
            selectionStore,
            snapshots,
            control,
            input,
            clipboard,
            clock);
    }

    private static JsonRpcDispatcher NewDispatcher(
        TokenStore tokens,
        FakeDesktopState? desktop = null,
        ControlSession? control = null)
    {
        var api = new FakeWindowApi();
        var registry = new WindowRegistry(api);
        var enumerator = new WindowEnumerator(api, registry);
        var selectionStore = new SelectionStore();
        var selection = new SelectionService(
            registry,
            new AllowAllAuthorization(),
            selectionStore,
            windows: enumerator,
            processPathResolver: pid => $@"C:\tests\{pid}.exe");
        var walker = new FakeUiaWalker();
        var focus = new FakeWin32();
        var snapshots = new SnapshotStore();
        var snapshotter = NewSnapshotter(walker);
        var actions = NewActions(
            walker,
            focus,
            registry,
            enumerator,
            selectionStore,
            snapshots,
            snapshotter,
            new FakeInputSender(),
            new FakeClipboard(),
            new DispatcherWaitClock(),
            liveNode: null);

        return new(
            tokens,
            new FakeMonitorProvider(),
            desktop ?? new FakeDesktopState(),
            enumerator,
            selection,
            registry,
            snapshotter,
            snapshots,
            control ?? new ControlSession(),
            actions.Click,
            actions.Type,
            actions.Key,
            actions.Scroll,
            actions.Wait,
            actions.ReadText,
            actions.Find,
            actions.Batch);
    }

    /// <summary>
    /// Builds every M2 action service from the same fakes the dispatcher tests
    /// already use, so no action can reach a real desktop, input sender, or
    /// clipboard.
    /// </summary>
    private static ActionDependencies NewActions(
        IUiaWalker walker,
        FakeWin32 focus,
        WindowRegistry registry,
        WindowEnumerator enumerator,
        SelectionStore selection,
        SnapshotStore snapshots,
        UiaSnapshotter snapshotter,
        FakeInputSender input,
        FakeClipboard clipboard,
        DispatcherWaitClock clock,
        IUiaNode? liveNode)
    {
        var resolver = new FakeUiaNodeResolver(liveNode);
        var preflight = new ActionPreflight(
            new ForegroundService(focus),
            selection,
            registry,
            new ExpectEvaluator(walker, focus),
            TimeSpan.FromMilliseconds(10));

        var click = new ClickAction(resolver, preflight, input, snapshots);
        var type = new TypeAction(resolver, preflight, input, clipboard, snapshots);
        var key = new KeyAction(preflight, input);
        var scroll = new ScrollAction(resolver, preflight, input, snapshots);
        var wait = new WaitAction(walker, enumerator, selection, registry, clock);
        var readText = new ReadTextAction(resolver, snapshots, selection, registry);
        var find = new FindAction(walker, snapshotter, snapshots, selection, registry);
        var batch = new BatchAction(
            click,
            type,
            key,
            scroll,
            wait,
            readText,
            snapshotter,
            snapshots,
            selection,
            registry);

        return new ActionDependencies(click, type, key, scroll, wait, readText, find, batch);
    }

    private static UiaSnapshotter NewSnapshotter(IUiaWalker walker, FakeWindowCapture? capture = null)
    {
        WindowCapture? captureImage = capture is null ? null : capture.Capture;
        return new(walker, _ => { }, _ => { }, captureImage);
    }

    private sealed record ActionDependencies(
        ClickAction Click,
        TypeAction Type,
        KeyAction Key,
        ScrollAction Scroll,
        WaitAction Wait,
        ReadTextAction ReadText,
        FindAction Find,
        BatchAction Batch);

    private sealed record SelectionHarness(
        JsonRpcDispatcher Dispatcher,
        WindowRegistry Registry,
        FakeWin32 Focus,
        FakeUiaWalker Walker,
        FakeWindowCapture Capture,
        FakeWindowApi Api,
        SelectionStore Selection,
        SnapshotStore Snapshots,
        ControlSession Control,
        FakeInputSender Input,
        FakeClipboard Clipboard,
        DispatcherWaitClock Clock)
    {
        /// <summary>Stores a snapshot generation with one addressable node.</summary>
        public void SaveNode(string nodeId, TreeModel node, int generation = 1)
        {
            var selected = Selection.Selected
                ?? throw new InvalidOperationException(
                    "SaveNode requires the harness to have selected a window.");
            Snapshots.Save(
                selected.WindowId,
                new Snapshot { Generation = generation },
                new Dictionary<string, TreeModel>(StringComparer.Ordinal)
                {
                    [nodeId] = node,
                },
                selected.Bounds);
        }
    }

    /// <summary>
    /// A clock that never sleeps: each delay advances the fake time and may run
    /// a hook, so a test can stop the control session between polls.
    /// </summary>
    private sealed class DispatcherWaitClock : IWaitClock
    {
        private readonly List<TimeSpan> _delays = [];

        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public IReadOnlyList<TimeSpan> Delays => _delays;

        public Action<int>? OnDelay { get; set; }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _delays.Add(delay);
            UtcNow += delay;
            OnDelay?.Invoke(_delays.Count);
            return Task.CompletedTask;
        }
    }

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
