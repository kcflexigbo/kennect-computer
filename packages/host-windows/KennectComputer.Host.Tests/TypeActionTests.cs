using KennectComputer.Host.Actions;
using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// Pins the type flow (DESIGN §6.4): paste saves the clipboard, sends Ctrl+V,
/// waits (bounded) for the target to consume the paste, then restores the
/// clipboard, so a slow target can never paste the user's previous clipboard.
/// Value sets the accessibility value or falls back to paste, keys sends
/// Unicode per code unit without touching the clipboard, and a node target
/// focuses the live element first. The selected window is always verified
/// foreground before any text is typed, expectations run first, and stale
/// nodes, focus refusals, and invalid requests send no input. Fakes stand in
/// for the resolver, foreground API, input sender, clipboard, and walker, and a
/// fake clock drives the consumption deadline, so no desktop, window, real
/// input, real clipboard, or real sleep is touched.
/// </summary>
public sealed class TypeActionTests
{
    private const ushort VkControl = 0x11;

    private static readonly Rect InitialBounds = new(100, 50, 3000, 2000);

    [Fact]
    public async Task PasteSavesSetsSendsTheChordThenDrainsAndRestoresTheClipboard()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.Clipboard.Text = "old text";

        var result = await harness.Action.ExecuteAsync(
            new TypeRequest { Text = "hello" },
            CancellationToken.None);

        Assert.Equal("type", result.Action);
        Assert.Equal("paste", result.Method);
        Assert.True(result.NeedsObservation);
        Assert.Equal(harness.SelectedWindow.WindowId, result.Window.WindowId);
        Assert.Equal(1, harness.Clipboard.GetCalls);
        Assert.Equal(["hello", "old text"], harness.Clipboard.Sets);
        Assert.Equal("old text", harness.Clipboard.Text);
        Assert.Equal(
            ["KeyDown:17", "KeyDown:86", "KeyUp:86", "KeyUp:17"],
            harness.Input.Calls);

        // Without a readable target the action drains the target window's
        // input queue after the full chord and only then restores the
        // clipboard. The drain observed all four key events.
        var drain = Assert.Single(harness.Drains);
        Assert.Equal(harness.Raw.Hwnd, drain.Hwnd);
        Assert.Equal(4, drain.InputCalls);
        Assert.Equal(TypeAction.DefaultPasteConsumptionTimeout, drain.Timeout);
        Assert.Equal(1, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task PasteWaitsForTheTargetTextBeforeRestoringTheClipboard()
    {
        var target = Edit();
        var live = FakeUiaNode.From(target);
        live.Value = string.Empty;
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);
        harness.Clipboard.Text = "old text";

        // The target processes the queued Ctrl+V after two polls, like a busy
        // app. The action must observe the text before restoring.
        var polls = 0;
        harness.Clock.OnDelay = () =>
        {
            polls++;
            if (polls == 2)
            {
                live.Value = "hello";
            }
        };

        string? contentAtRestore = null;
        harness.Clipboard.OnSet = text =>
        {
            if (text == "old text")
            {
                contentAtRestore = live.Value;
            }
        };

        var result = await harness.Action.ExecuteAsync(
            new TypeRequest { Text = "hello", NodeId = "n1:1" },
            CancellationToken.None);

        Assert.Equal("paste", result.Method);
        Assert.Equal(["hello", "old text"], harness.Clipboard.Sets);
        Assert.Equal("hello", contentAtRestore);
        Assert.Equal(2, harness.Clock.Delays);
        Assert.Empty(harness.Drains);
    }

    [Fact]
    public async Task PasteRestoresTheClipboardWhenTheTargetNeverShowsThePaste()
    {
        var target = Edit();
        var live = FakeUiaNode.From(target);
        live.Value = string.Empty;
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);
        harness.Clipboard.Text = "old text";

        string? contentAtRestore = null;
        harness.Clipboard.OnSet = text =>
        {
            if (text == "old text")
            {
                contentAtRestore = live.Value;
            }
        };

        var result = await harness.Action.ExecuteAsync(
            new TypeRequest { Text = "hello", NodeId = "n1:1" },
            CancellationToken.None);

        Assert.Equal("paste", result.Method);
        Assert.Equal(["hello", "old text"], harness.Clipboard.Sets);
        Assert.Equal(string.Empty, contentAtRestore);
        Assert.True(harness.Clock.Delays > 0, "The bounded wait did not poll.");
        Assert.Empty(harness.Drains);
    }

    [Fact]
    public async Task CancellationDuringTheConsumptionWaitStillRestoresTheClipboard()
    {
        var target = Edit();
        var live = FakeUiaNode.From(target);
        live.Value = string.Empty;
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);
        harness.Clipboard.Text = "old text";

        using var cancellation = new CancellationTokenSource();
        harness.Clock.OnDelay = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = "hello", NodeId = "n1:1" },
                cancellation.Token));

        Assert.Equal(["hello", "old text"], harness.Clipboard.Sets);
        Assert.Equal("old text", harness.Clipboard.Text);
    }

    [Fact]
    public async Task AnUnreadableNodeTargetFallsBackToDrainingTheInputQueue()
    {
        var target = Edit();
        var live = FakeUiaNode.From(target) with
        {
            SupportsGetText = false,
            SupportsGetValue = false,
        };
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);
        harness.Clipboard.Text = "old text";

        var result = await harness.Action.ExecuteAsync(
            new TypeRequest { Text = "hello", NodeId = "n1:1" },
            CancellationToken.None);

        Assert.Equal("paste", result.Method);
        Assert.Equal(["hello", "old text"], harness.Clipboard.Sets);
        Assert.Single(harness.Drains);
        Assert.Equal(0, harness.Clock.Delays);
    }

    [Fact]
    public async Task PasteRestoresTheClipboardWhenTheChordThrows()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.Clipboard.Text = "old text";
        harness.Input.BeforeKeyDown = () => throw new InvalidOperationException("input blocked");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = "hello" },
                CancellationToken.None));

        Assert.Equal("input blocked", failure.Message);
        Assert.Equal(["hello", "old text"], harness.Clipboard.Sets);
        Assert.Equal("old text", harness.Clipboard.Text);
    }

    [Fact]
    public async Task AChordFailureReleasesTheControlKey()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.Clipboard.Text = "old text";

        var releaseFailed = false;
        harness.Input.BeforeKeyUp = () =>
        {
            if (!releaseFailed)
            {
                releaseFailed = true;
                throw new InvalidOperationException("send failed");
            }
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = "hello" },
                CancellationToken.None));

        Assert.Equal("send failed", failure.Message);

        // V's release failed, but Ctrl must still be released so the system is
        // not left with a stuck modifier and a repeating paste shortcut.
        Assert.Equal((VkControl, false), harness.Input.Keys[^1]);
        Assert.Equal(["hello", "old text"], harness.Clipboard.Sets);
        Assert.Equal("old text", harness.Clipboard.Text);
    }

    [Fact]
    public async Task PasteDoesNotLeaveTheTypedTextWhenTheClipboardHadNoText()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.Clipboard.Text = null;

        await harness.Action.ExecuteAsync(
            new TypeRequest { Text = "hello" },
            CancellationToken.None);

        Assert.Equal(["hello", string.Empty], harness.Clipboard.Sets);
        Assert.Equal(string.Empty, harness.Clipboard.Text);
    }

    [Fact]
    public async Task AFailedClipboardSaveIsInvalidAndTypesNothing()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.Clipboard.Text = "old text";
        harness.Clipboard.GetFailure = new InvalidOperationException("clipboard busy");

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = "hello" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Clipboard.Sets);
        Assert.Equal("old text", harness.Clipboard.Text);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task AFailedRestoreClearsTheTypedText()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.Clipboard.Text = "old text";

        // The restore is the second write; the fallback clear is the third.
        harness.Clipboard.FailSetAttempt = 2;

        var result = await harness.Action.ExecuteAsync(
            new TypeRequest { Text = "hello" },
            CancellationToken.None);

        Assert.Equal("paste", result.Method);
        Assert.Equal(["hello", string.Empty], harness.Clipboard.Sets);
        Assert.Equal(string.Empty, harness.Clipboard.Text);
    }

    [Fact]
    public async Task ValueUsesThePatternWithoutClipboardOrInput()
    {
        var target = Edit();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new TypeRequest
            {
                Text = "hello",
                Method = TypeMethod.Value,
                NodeId = "n1:1",
            },
            CancellationToken.None);

        Assert.Equal("value", result.Method);
        Assert.Equal(["TryFocus", "TrySetValue:hello"], live.Calls);
        Assert.Equal("hello", live.Value);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Clipboard.GetCalls);
        Assert.Empty(harness.Clipboard.Sets);
    }

    [Fact]
    public async Task ValueFallsBackToPasteWhenThePatternIsUnsupported()
    {
        var target = Edit();
        var live = FakeUiaNode.From(target) with { SupportsSetValue = false };
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);
        harness.Clipboard.Text = "old text";

        var result = await harness.Action.ExecuteAsync(
            new TypeRequest
            {
                Text = "hello",
                Method = TypeMethod.Value,
                NodeId = "n1:1",
            },
            CancellationToken.None);

        Assert.Equal("paste", result.Method);

        // The fallback paste reads the pre-chord content once to establish a
        // baseline for paste-consumption detection.
        Assert.Equal(
            ["TryFocus", "TrySetValue:hello", "TryGetValue", "TryGetText"],
            live.Calls);
        Assert.Equal(["hello", "old text"], harness.Clipboard.Sets);
        Assert.Equal(
            ["KeyDown:17", "KeyDown:86", "KeyUp:86", "KeyUp:17"],
            harness.Input.Calls);
    }

    [Fact]
    public async Task KeysTypesUnicodeWithoutTouchingTheClipboard()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        var result = await harness.Action.ExecuteAsync(
            new TypeRequest { Text = "abc", Method = TypeMethod.Keys },
            CancellationToken.None);

        Assert.Equal("keys", result.Method);
        Assert.Equal(["a", "b", "c"], harness.Input.Text);
        Assert.Empty(harness.Input.Keys);
        Assert.Equal(0, harness.Clipboard.GetCalls);
        Assert.Empty(harness.Clipboard.Sets);
    }

    [Fact]
    public async Task ANodeTargetFocusesTheLiveNodeBeforeTyping()
    {
        var target = Edit();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var focusedBeforeTyping = false;
        harness.Input.BeforeUnicodeText = () =>
            focusedBeforeTyping = live.Calls.Contains("TryFocus");

        await harness.Action.ExecuteAsync(
            new TypeRequest
            {
                Text = "hi",
                Method = TypeMethod.Keys,
                NodeId = "n1:1",
            },
            CancellationToken.None);

        Assert.True(focusedBeforeTyping);
        Assert.Equal(["TryFocus"], live.Calls);
        Assert.Equal(1, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task AStaleNodeGenerationReturnsStaleSnapshotAndSendsNoInput()
    {
        var target = Edit();
        var live = FakeUiaNode.From(target);
        var resolver = new FakeUiaNodeResolver(live);
        var harness = Harness.Create(resolver);
        harness.SaveNode("n1:1", target);
        harness.Clipboard.Text = "old text";

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = "hello", NodeId = "n2:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Clipboard.GetCalls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
        Assert.Null(resolver.LastTarget);
    }

    [Fact]
    public async Task AChangedLiveFingerprintReturnsStaleSnapshotAndSendsNoInput()
    {
        var target = Edit();
        var moved = target with
        {
            Bounds = new Rect(
                target.Bounds.X + 5,
                target.Bounds.Y,
                target.Bounds.Width,
                target.Bounds.Height),
        };
        var harness = Harness.Create(new FakeUiaNodeResolver(FakeUiaNode.From(moved)));
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = "hello", NodeId = "n1:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Clipboard.GetCalls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task AFailedExpectationSendsNoInputAndPreservesTheClipboard()
    {
        var harness = Harness.Create(
            new FakeUiaNodeResolver(null),
            walker: new FakeUiaWalker(new TreeModel { Role = "window", Name = "Notepad" }));
        harness.Clipboard.Text = "old text";

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest
                {
                    Text = "hello",
                    Expect = [new ExpectNode { Name = "Missing" }],
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.ExpectationFailed, failure.Code);
        Assert.Equal("node:Missing", failure.FailedPredicate);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Clipboard.GetCalls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task ACjkPasteReachesTheClipboardUnchanged()
    {
        const string text = "你好，世界";
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.Clipboard.Text = "old text";

        await harness.Action.ExecuteAsync(
            new TypeRequest { Text = text },
            CancellationToken.None);

        Assert.Equal([text, "old text"], harness.Clipboard.Sets);
        Assert.Equal(
            ["KeyDown:17", "KeyDown:86", "KeyUp:86", "KeyUp:17"],
            harness.Input.Calls);
    }

    [Fact]
    public async Task ACjkKeysStringReachesTheInputOneCodeUnitAtATime()
    {
        const string text = "你好𠮷";
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        await harness.Action.ExecuteAsync(
            new TypeRequest { Text = text, Method = TypeMethod.Keys },
            CancellationToken.None);

        // A surrogate pair is two UTF-16 code units and therefore two sends.
        Assert.Equal(text.Length, harness.Input.Text.Count);
        Assert.Equal(text, string.Concat(harness.Input.Text));
        Assert.Equal(0, harness.Clipboard.GetCalls);
    }

    [Theory]
    [InlineData(TypeMethod.Paste, "paste")]
    [InlineData(TypeMethod.Value, "value")]
    [InlineData(TypeMethod.Keys, "keys")]
    public async Task TheResultReportsTheMethodUsedAndNeedsObservation(
        TypeMethod method,
        string expected)
    {
        var target = Edit();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);
        harness.Clipboard.Text = "old text";

        var result = await harness.Action.ExecuteAsync(
            new TypeRequest { Text = "hi", Method = method, NodeId = "n1:1" },
            CancellationToken.None);

        Assert.Equal("type", result.Action);
        Assert.Equal(expected, result.Method);
        Assert.True(result.NeedsObservation);
        Assert.Equal(harness.SelectedWindow.WindowId, result.Window.WindowId);
    }

    [Fact]
    public async Task AFocusRefusalReturnsFocusRefusedAndSendsNoInput()
    {
        var harness = Harness.Create(
            new FakeUiaNodeResolver(null),
            foregroundSucceeds: false);
        harness.Clipboard.Text = "old text";

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = "hello" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.FocusRefused, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Clipboard.GetCalls);
        Assert.Empty(harness.Clipboard.Sets);
    }

    [Fact]
    public async Task ANodeThatRefusesFocusReturnsFocusRefusedAndSendsNoInput()
    {
        var target = Edit();
        var live = FakeUiaNode.From(target) with { SupportsFocus = false };
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);
        harness.Clipboard.Text = "old text";

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = "hello", NodeId = "n1:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.FocusRefused, failure.Code);
        Assert.Equal(["TryFocus"], live.Calls);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Clipboard.GetCalls);
        Assert.Empty(harness.Clipboard.Sets);
    }

    [Fact]
    public async Task ValueWithoutANodeIsInvalidAndSendsNoInput()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.Clipboard.Text = "old text";

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = "hello", Method = TypeMethod.Value },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Clipboard.GetCalls);
    }

    [Fact]
    public async Task EmptyTextIsInvalidAndSendsNoInput()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.Clipboard.Text = "old text";

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = string.Empty },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Clipboard.GetCalls);
    }

    [Fact]
    public async Task NoSelectedWindowIsInvalidAndSendsNoInput()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null), select: false);
        harness.Clipboard.Text = "old text";

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = "hello" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Clipboard.GetCalls);
    }

    [Fact]
    public async Task HonorsCancellationBeforeInput()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.Clipboard.Text = "old text";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Action.ExecuteAsync(
                new TypeRequest { Text = "hello" },
                cancellation.Token));

        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Clipboard.GetCalls);
        Assert.Empty(harness.Clipboard.Sets);
    }

    private static TreeModel Edit() => new()
    {
        Role = "edit",
        Name = "Message",
        Identifier = "message-input",
        Bounds = new Rect(10, 20, 300, 32),
    };

    /// <summary>
    /// One selected window plus every type dependency, wired exactly as the
    /// dispatcher will wire them. The focus timeout is short so refusal tests
    /// do not wait on the production one-second deadline.
    /// </summary>
    private sealed class Harness
    {
        private Harness(
            IUiaNodeResolver resolver,
            FakeUiaWalker? walker,
            bool foregroundSucceeds,
            bool select)
        {
            Raw = new RawWindow(1, 100, "Notepad", "Notepad", InitialBounds)
            {
                ClassName = "NotepadClass",
            };

            Windows = new FakeWindowApi(Raw);
            Registry = new WindowRegistry(Windows);
            SelectedWindow = Registry.Register(Raw);
            Selection = new SelectionStore();

            if (select)
            {
                Selection.Select(SelectedWindow);
            }

            Snapshots = new SnapshotStore();
            Input = new FakeInputSender();
            Clipboard = new FakeClipboard();
            Clock = new FakeTypeClock();
            Foreground = new FakeWin32
            {
                InitialForeground = 99,
                ForegroundSucceeds = foregroundSucceeds,
            };
            Walker = walker
                ?? new FakeUiaWalker(new TreeModel { Role = "window", Name = "Notepad" });

            Action = new TypeAction(
                resolver,
                new ActionPreflight(
                    new ForegroundService(Foreground),
                    Selection,
                    Registry,
                    new ExpectEvaluator(Walker, Foreground),
                    TimeSpan.FromMilliseconds(10)),
                Input,
                Clipboard,
                Snapshots,
                Clock,
                drainInputQueue: (hwnd, timeout, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Drains.Add(new DrainCall(hwnd, timeout, Input.Calls.Count));
                });
        }

        public RawWindow Raw { get; }

        public FakeWindowApi Windows { get; }

        public WindowRegistry Registry { get; }

        public WindowInfo SelectedWindow { get; }

        public SelectionStore Selection { get; }

        public SnapshotStore Snapshots { get; }

        public FakeInputSender Input { get; }

        public FakeClipboard Clipboard { get; }

        public FakeTypeClock Clock { get; }

        /// <summary>Every substituted input-queue drain, in order.</summary>
        public List<DrainCall> Drains { get; } = [];

        public FakeWin32 Foreground { get; }

        public FakeUiaWalker Walker { get; }

        public TypeAction Action { get; }

        public static Harness Create(
            IUiaNodeResolver resolver,
            FakeUiaWalker? walker = null,
            bool foregroundSucceeds = true,
            bool select = true)
            => new(resolver, walker, foregroundSucceeds, select);

        /// <summary>Stores a snapshot generation with one addressable node.</summary>
        public void SaveNode(string nodeId, TreeModel node, int generation = 1)
        {
            var nodes = new Dictionary<string, TreeModel>(StringComparer.Ordinal)
            {
                [nodeId] = node,
            };

            var snapshot = new Snapshot { Generation = generation };

            Snapshots.Save(
                SelectedWindow.WindowId,
                snapshot,
                nodes,
                SelectedWindow.Bounds);
        }
    }
}

/// <summary>
/// A deterministic clipboard for type tests. It records every successful write
/// in order, can fail reads (simulated contention), and can fail one specific
/// write attempt by 1-based number so restore failures are reproducible.
/// </summary>
internal sealed class FakeClipboard : IClipboard
{
    private int _setAttempts;

    public string? Text { get; set; }

    public List<string> Sets { get; } = [];

    public int GetCalls { get; private set; }

    public Exception? GetFailure { get; set; }

    /// <summary>1-based successful-or-not <see cref="SetText"/> attempt to fail; 0 disables.</summary>
    public int FailSetAttempt { get; set; }

    /// <summary>Runs after each successful write, so tests can observe restore ordering.</summary>
    public Action<string>? OnSet { get; set; }

    public string? GetText()
    {
        GetCalls++;

        if (GetFailure is not null)
        {
            throw GetFailure;
        }

        return Text;
    }

    public void SetText(string text)
    {
        _setAttempts++;

        if (_setAttempts == FailSetAttempt)
        {
            throw new InvalidOperationException("clipboard busy");
        }

        Sets.Add(text);
        Text = text;
        OnSet?.Invoke(text);
    }
}

/// <summary>One substituted queue drain, with the key events already sent.</summary>
internal sealed record DrainCall(nint Hwnd, TimeSpan Timeout, int InputCalls);

/// <summary>
/// A clock that never sleeps: each delay advances fake time and may run a hook,
/// so paste-consumption waits run deterministically and instantly.
/// </summary>
internal sealed class FakeTypeClock : IWaitClock
{
    public DateTimeOffset UtcNow { get; private set; } =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public int Delays { get; private set; }

    public Action? OnDelay { get; set; }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays++;
        UtcNow += delay;
        OnDelay?.Invoke();
        return Task.CompletedTask;
    }
}
