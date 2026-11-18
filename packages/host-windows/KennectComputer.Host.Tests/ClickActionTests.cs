using KennectComputer.Host.Actions;
using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// Pins the click flow (DESIGN §6.1–§6.3): the selected window is resolved,
/// expectations run before any input, node clicks re-resolve a live element
/// and prefer a semantic pattern over a bounds-center click, coordinates map
/// through the snapshot's scale source bounds, and every failure path sends
/// no input. Fakes stand in for the resolver, foreground API, input sender,
/// and walker, so no desktop, window, or real input is touched.
/// </summary>
public sealed class ClickActionTests
{
    private static readonly Rect InitialBounds = new(100, 50, 3000, 2000);

    [Fact]
    public async Task SemanticClickInvokesThePatternAndSendsNoMouseInput()
    {
        var target = Button();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { NodeId = "n1:1" },
            CancellationToken.None);

        Assert.Equal("click", result.Action);
        Assert.Equal("pattern", result.Method);
        Assert.True(result.NeedsObservation);
        Assert.Equal(harness.SelectedWindow.WindowId, result.Window.WindowId);
        Assert.Equal(["TryInvoke"], live.Calls);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(1, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task TheFirstSupportedPatternWinsInOrder()
    {
        var target = Button();
        var live = FakeUiaNode.From(target) with { SupportsInvoke = false };
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { NodeId = "n1:1" },
            CancellationToken.None);

        Assert.Equal("pattern", result.Method);
        Assert.Equal(["TryInvoke", "TryToggle"], live.Calls);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ANodeWithoutASupportedPatternClicksItsLiveBoundsCenter()
    {
        var target = Button();
        var live = FakeUiaNode.From(target) with
        {
            SupportsInvoke = false,
            SupportsToggle = false,
            SupportsSelect = false,
            SupportsExpand = false,
        };
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { NodeId = "n1:1", Button = MouseButton.Right, Count = 2 },
            CancellationToken.None);

        Assert.Equal("bounds", result.Method);

        // A non-default button or count is a mouse gesture, not a semantic
        // activation, so no pattern is attempted.
        Assert.Empty(live.Calls);

        // The captured button is 120x32 at (110, 70): its center is (170, 86).
        Assert.Equal([(170, 86)], harness.Input.Moves);
        Assert.Equal([(MouseButton.Right, 1), (MouseButton.Right, 1)], harness.Input.Clicks);
    }

    [Fact]
    public async Task ARightButtonNodeClickSkipsSemanticPatterns()
    {
        var target = Button();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { NodeId = "n1:1", Button = MouseButton.Right },
            CancellationToken.None);

        Assert.Equal("bounds", result.Method);
        Assert.Empty(live.Calls);
        Assert.Equal([(MouseButton.Right, 1)], harness.Input.Clicks);
    }

    [Fact]
    public async Task ACountGreaterThanOneSkipsSemanticPatterns()
    {
        var target = Button();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { NodeId = "n1:1", Count = 3 },
            CancellationToken.None);

        Assert.Equal("bounds", result.Method);
        Assert.Empty(live.Calls);
        Assert.Equal(
            [(MouseButton.Left, 1), (MouseButton.Left, 1), (MouseButton.Left, 1)],
            harness.Input.Clicks);
    }

    [Fact]
    public async Task ModifierClickPressesBeforeAndReleasesInReverseAfterTheClick()
    {
        var target = Button();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest
            {
                NodeId = "n1:1",
                Modifiers = ["Ctrl", "Shift"],
            },
            CancellationToken.None);

        Assert.Equal("bounds", result.Method);
        Assert.Empty(live.Calls);
        Assert.Equal(
            [
                "MoveTo:170,86",
                "KeyDown:17",
                "KeyDown:16",
                "Click:Left:1",
                "KeyUp:16",
                "KeyUp:17",
            ],
            harness.Input.Calls);
    }

    [Fact]
    public async Task AFailedModifiedClickStillReleasesEveryModifier()
    {
        var target = Button();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);
        harness.Input.BeforeClick = () => throw new InvalidOperationException("click failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest
                {
                    NodeId = "n1:1",
                    Modifiers = ["Ctrl", "Shift"],
                },
                CancellationToken.None));

        Assert.Equal(
            ["MoveTo:170,86", "KeyDown:17", "KeyDown:16", "KeyUp:16", "KeyUp:17"],
            harness.Input.Calls);
    }

    [Fact]
    public async Task AnUnknownModifierIsInvalidAndSendsNoInput()
    {
        var target = Button();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { NodeId = "n1:1", Modifiers = ["Hyper"] },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
        Assert.Empty(live.Calls);
    }

    [Fact]
    public async Task ANodeWithEmptyBoundsReturnsStaleSnapshotAndSendsNoInput()
    {
        var target = Button() with { Bounds = new Rect(110, 70, 0, 0) };
        var live = FakeUiaNode.From(target) with
        {
            SupportsInvoke = false,
            SupportsToggle = false,
            SupportsSelect = false,
            SupportsExpand = false,
        };
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { NodeId = "n1:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ANodeCenterOutsideTheWindowReturnsStaleSnapshotAndSendsNoInput()
    {
        // The live element matches its fingerprint but sits over another
        // window; a bounds-center click there would hit the wrong app.
        var target = Button() with { Bounds = new Rect(5000, 5000, 120, 32) };
        var live = FakeUiaNode.From(target) with
        {
            SupportsInvoke = false,
            SupportsToggle = false,
            SupportsSelect = false,
            SupportsExpand = false,
        };
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { NodeId = "n1:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ImageSpaceClickMapsThroughTheStoredScaleAndClicksThePhysicalPoint()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.SaveSnapshot(generation: 1, image: true, scale: 1.5);

        // Space is omitted: the host default is image space.
        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { X = 10, Y = 20 },
            CancellationToken.None);

        Assert.Equal("click", result.Action);
        Assert.Equal("coordinates", result.Method);
        Assert.True(result.NeedsObservation);
        Assert.Equal([(115, 80)], harness.Input.Moves);
        Assert.Equal([(MouseButton.Left, 1)], harness.Input.Clicks);
    }

    [Fact]
    public async Task ACoordinateClickSnapsToTheLiveElementUnderThePoint()
    {
        var resolver = new FakeUiaNodeResolver(null)
        {
            SnapTarget = new SnapTarget("text", "Don't lie to me", new Rect(300, 200, 120, 26)),
        };
        var harness = Harness.Create(resolver);

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { X = 290, Y = 210, Space = CoordinateSpace.Screen },
            CancellationToken.None);

        Assert.Equal("coordinates", result.Method);
        Assert.NotNull(result.SnappedTo);
        Assert.Equal("text", result.SnappedTo.Role);
        Assert.Equal("Don't lie to me", result.SnappedTo.Name);
        Assert.Equal([(290, 210)], resolver.SnapCalls);

        // The element's center (360, 213), not the estimated point.
        Assert.Equal([(360, 213)], harness.Input.Moves);
    }

    [Fact]
    public async Task AnImageSpaceClickSnapsAfterMapping()
    {
        var resolver = new FakeUiaNodeResolver(null)
        {
            SnapTarget = new SnapTarget("text", "Naaaah", new Rect(120, 70, 100, 30)),
        };
        var harness = Harness.Create(resolver);
        harness.SaveSnapshot(generation: 1, image: true, scale: 1.5);

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { X = 10, Y = 20 },
            CancellationToken.None);

        // The image point maps to (115, 80) before the snap is queried.
        Assert.Equal([(115, 80)], resolver.SnapCalls);
        Assert.Equal("text", result.SnappedTo?.Role);

        // The snapped element is 100x30 at (120, 70): its center is (170, 85).
        Assert.Equal([(170, 85)], harness.Input.Moves);
    }

    [Fact]
    public async Task ASnapTargetOutsideTheWindowIsIgnored()
    {
        var resolver = new FakeUiaNodeResolver(null)
        {
            SnapTarget = new SnapTarget("text", "Elsewhere", new Rect(0, 0, 100, 30)),
        };
        var harness = Harness.Create(resolver);

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { X = 300, Y = 200, Space = CoordinateSpace.Screen },
            CancellationToken.None);

        Assert.Null(result.SnappedTo);
        Assert.Equal([(300, 200)], harness.Input.Moves);
    }

    [Fact]
    public async Task ATextSelectorClicksTheSingleMatchingElement()
    {
        var live = FakeUiaNode.From(Button()) with
        {
            SupportsInvoke = false,
            SupportsToggle = false,
            SupportsSelect = false,
            SupportsExpand = false,
        };
        var resolver = new FakeUiaNodeResolver(live) { TextMatches = [live] };
        var harness = Harness.Create(resolver);

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { Text = "Send" },
            CancellationToken.None);

        Assert.Equal("text", result.Method);
        Assert.Equal("Send", resolver.LastText);
        Assert.Null(resolver.LastRole);
        Assert.Equal([(170, 86)], harness.Input.Moves);
        Assert.Equal([(MouseButton.Left, 1)], harness.Input.Clicks);
        Assert.Equal(1, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task ATextSelectorUsesASemanticPatternWhenAvailable()
    {
        var live = FakeUiaNode.From(Button());
        var harness = Harness.Create(new FakeUiaNodeResolver(live) { TextMatches = [live] });

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { Text = "Send" },
            CancellationToken.None);

        Assert.Equal("text", result.Method);
        Assert.Equal(["TryInvoke"], live.Calls);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ATextSelectorWithNoMatchIsStaleAndSendsNoInput()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { Text = "Missing" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task AnAmbiguousTextSelectorIsInvalidAndSendsNoInput()
    {
        var first = FakeUiaNode.From(Button());
        var second = FakeUiaNode.From(Button());
        var harness = Harness.Create(
            new FakeUiaNodeResolver(null) { TextMatches = [first, second] });

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { Text = "Send" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task ATextSelectorPassesTheRoleToTheResolver()
    {
        var live = FakeUiaNode.From(Button());
        var resolver = new FakeUiaNodeResolver(live) { TextMatches = [live] };
        var harness = Harness.Create(resolver);

        await harness.Action.ExecuteAsync(
            new ClickRequest { Text = "Send", Role = "Button" },
            CancellationToken.None);

        Assert.Equal("button", resolver.LastRole);
    }

    [Fact]
    public async Task TextAndCoordinatesIsInvalid()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { Text = "Send", X = 1, Y = 2 },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ImageSpaceClickWithoutASnapshotImageIsInvalid()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.SaveSnapshot(generation: 1, image: false);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { X = 10, Y = 20, Space = CoordinateSpace.Image },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task AStaleNodeGenerationReturnsStaleSnapshotAndSendsNoInput()
    {
        var target = Button();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { NodeId = "n2:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
        Assert.Empty(live.Calls);
    }

    [Fact]
    public async Task AChangedLiveFingerprintReturnsStaleSnapshotAndSendsNoInput()
    {
        var target = Button();
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
                new ClickRequest { NodeId = "n1:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task AMovedWindowWithImageSpaceCoordinatesReturnsStaleSnapshot()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.SaveSnapshot(generation: 1, image: true, scale: 1.5);
        harness.MoveWindow(new Rect(200, 100, 800, 600));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { X = 10, Y = 20, Space = CoordinateSpace.Image },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task AScreenSpaceClickBypassesTheBoundsChangeCheck()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.SaveSnapshot(generation: 1, image: true, scale: 1.5);
        harness.MoveWindow(new Rect(200, 100, 800, 600));

        var result = await harness.Action.ExecuteAsync(
            new ClickRequest { X = 300, Y = 200, Space = CoordinateSpace.Screen },
            CancellationToken.None);

        Assert.Equal("coordinates", result.Method);
        Assert.Equal([(300, 200)], harness.Input.Moves);
        Assert.Equal([(MouseButton.Left, 1)], harness.Input.Clicks);
        Assert.Equal(1, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task AScreenSpaceClickStillVerifiesForeground()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null), foregroundSucceeds: false);
        harness.SaveSnapshot(generation: 1, image: true, scale: 1.5);
        harness.MoveWindow(new Rect(200, 100, 800, 600));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { X = 300, Y = 200, Space = CoordinateSpace.Screen },
                CancellationToken.None));

        Assert.Equal(ErrorCode.FocusRefused, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task AFocusRefusalReturnsFocusRefusedAndSendsNoInput()
    {
        var target = Button();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(
            new FakeUiaNodeResolver(live),
            foregroundSucceeds: false);
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { NodeId = "n1:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.FocusRefused, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Empty(live.Calls);
    }

    [Fact]
    public async Task AFailedExpectationReturnsExpectationFailedAndSendsNoInput()
    {
        var target = Button();
        var live = FakeUiaNode.From(target);
        var walker = new FakeUiaWalker(new TreeModel { Role = "window", Name = "Notepad" });
        var resolver = new FakeUiaNodeResolver(live);
        var harness = Harness.Create(resolver, walker: walker);
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest
                {
                    NodeId = "n1:1",
                    Expect = [new ExpectNode { Name = "Missing" }],
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.ExpectationFailed, failure.Code);
        Assert.Equal("node:Missing", failure.FailedPredicate);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
        Assert.Empty(live.Calls);
        Assert.Null(resolver.LastTarget);
    }

    [Fact]
    public async Task NoSelectedWindowIsInvalidAndSendsNoInput()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null), select: false);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { X = 300, Y = 200, Space = CoordinateSpace.Screen },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task BothANodeAndCoordinatesIsInvalid()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest { NodeId = "n1:1", X = 10, Y = 20 },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task AClickWithoutATargetIsInvalid()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest(),
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ANonPositiveClickCountIsInvalid()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest
                {
                    X = 300,
                    Y = 200,
                    Space = CoordinateSpace.Screen,
                    Count = 0,
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task HonorsCancellationBeforeInput()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Action.ExecuteAsync(
                new ClickRequest
                {
                    X = 300,
                    Y = 200,
                    Space = CoordinateSpace.Screen,
                },
                cancellation.Token));

        Assert.Empty(harness.Input.Calls);
    }

    private static TreeModel Button() => new()
    {
        Role = "button",
        Name = "Send",
        Identifier = "send",
        Bounds = new Rect(110, 70, 120, 32),
        Actions = ["press"],
    };

    /// <summary>
    /// One selected window plus every click dependency, wired exactly as the
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
            Foreground = new FakeWin32
            {
                InitialForeground = 99,
                ForegroundSucceeds = foregroundSucceeds,
            };
            Walker = walker
                ?? new FakeUiaWalker(new TreeModel { Role = "window", Name = "Notepad" });

            Action = new ClickAction(
                resolver,
                new ActionPreflight(
                    new ForegroundService(Foreground),
                    Selection,
                    Registry,
                    new ExpectEvaluator(Walker, Foreground),
                    TimeSpan.FromMilliseconds(10)),
                Input,
                Snapshots);
        }

        public RawWindow Raw { get; }

        public FakeWindowApi Windows { get; }

        public WindowRegistry Registry { get; }

        public WindowInfo SelectedWindow { get; }

        public SelectionStore Selection { get; }

        public SnapshotStore Snapshots { get; }

        public FakeInputSender Input { get; }

        public FakeWin32 Foreground { get; }

        public FakeUiaWalker Walker { get; }

        public ClickAction Action { get; }

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

            SaveSnapshot(generation, image: false, nodes: nodes);
        }

        /// <summary>Stores snapshot state as a snapshot with or without an image.</summary>
        public void SaveSnapshot(
            int generation,
            bool image,
            double scale = 1.5,
            IReadOnlyDictionary<string, TreeModel>? nodes = null)
        {
            var snapshot = new Snapshot
            {
                Generation = generation,
                Image = image
                    ? new ImagePayload
                    {
                        Data = "x",
                        MimeType = "image/png",
                        Scale = scale,
                        SourceBounds = InitialBounds,
                    }
                    : null,
            };

            Snapshots.Save(
                SelectedWindow.WindowId,
                snapshot,
                nodes ?? new Dictionary<string, TreeModel>(StringComparer.Ordinal),
                SelectedWindow.Bounds);
        }

        /// <summary>Simulates the OS reporting the same window at new bounds.</summary>
        public void MoveWindow(Rect bounds)
            => Windows.Replace(Raw with { Bounds = bounds });
    }
}

/// <summary>
/// Records the high-level input an action requests. No real input is sent;
/// later action tests reuse it.
/// </summary>
internal sealed class FakeInputSender : IInputSender
{
    public List<string> Calls { get; } = [];

    public List<(int X, int Y)> Moves { get; } = [];

    public List<(MouseButton Button, int Count)> Clicks { get; } = [];

    public List<(int Delta, bool Horizontal)> Scrolls { get; } = [];

    public List<(ushort VirtualKey, bool Down)> Keys { get; } = [];

    public List<string> Text { get; } = [];

    /// <summary>Runs before a key-down is recorded; tests inject failures here.</summary>
    public Action? BeforeKeyDown { get; set; }

    /// <summary>Runs before a key-up is recorded; tests inject failures here.</summary>
    public Action? BeforeKeyUp { get; set; }

    /// <summary>Runs before a click is recorded; tests inject failures or observe order.</summary>
    public Action? BeforeClick { get; set; }

    /// <summary>Runs before typed text is recorded; tests inject failures or observe order.</summary>
    public Action? BeforeUnicodeText { get; set; }

    public void MoveTo(int x, int y)
    {
        Calls.Add($"MoveTo:{x},{y}");
        Moves.Add((x, y));
    }

    public void Click(MouseButton button, int count)
    {
        BeforeClick?.Invoke();
        Calls.Add($"Click:{button}:{count}");
        Clicks.Add((button, count));
    }

    public void Scroll(int delta, bool horizontal)
    {
        Calls.Add($"Scroll:{delta}:{horizontal}");
        Scrolls.Add((delta, horizontal));
    }

    public void KeyDown(ushort virtualKey)
    {
        BeforeKeyDown?.Invoke();
        Calls.Add($"KeyDown:{virtualKey}");
        Keys.Add((virtualKey, true));
    }

    public void KeyUp(ushort virtualKey)
    {
        BeforeKeyUp?.Invoke();
        Calls.Add($"KeyUp:{virtualKey}");
        Keys.Add((virtualKey, false));
    }

    public void UnicodeText(string text)
    {
        BeforeUnicodeText?.Invoke();
        Calls.Add($"UnicodeText:{text}");
        Text.Add(text);
    }
}
