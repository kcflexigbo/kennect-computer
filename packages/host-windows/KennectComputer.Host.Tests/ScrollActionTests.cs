using KennectComputer.Host.Actions;
using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// Pins the scroll flow (DESIGN §6.2, §6.6): a node target scrolls at the live
/// element's bounds center, coordinates map through the snapshot's scale and
/// source bounds (image space) or are already physical (screen space), and the
/// direction and amount become a wheel delta of 120 per notch (negative for
/// down and left, horizontal for left and right). Expectations run before any
/// focus or input, stale targets and refusals send no scroll, and the amount is
/// clamped to 1..100. Fakes stand in for the resolver, foreground API, input
/// sender, and walker, so no desktop, window, or real input is touched.
/// </summary>
public sealed class ScrollActionTests
{
    private static readonly Rect InitialBounds = new(100, 50, 3000, 2000);

    [Fact]
    public async Task ScrollDownThreeAtANodeMovesToItsCenterAndSendsMinus360()
    {
        var target = List();
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ScrollRequest
            {
                NodeId = "n1:1",
                Direction = ScrollDirection.Down,
                Amount = 3,
            },
            CancellationToken.None);

        Assert.Equal("scroll", result.Action);
        Assert.Equal("wheel", result.Method);
        Assert.True(result.NeedsObservation);
        Assert.Equal(harness.SelectedWindow.WindowId, result.Window.WindowId);

        // The captured list is 120x32 at (110, 70): its live center is (170, 86).
        Assert.Equal([(170, 86)], harness.Input.Moves);
        Assert.Equal([(-360, false)], harness.Input.Scrolls);
        Assert.Equal(1, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task ANodeWithEmptyBoundsReturnsStaleSnapshotAndSendsNoInput()
    {
        var target = List() with { Bounds = new Rect(110, 70, 0, 0) };
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ScrollRequest { NodeId = "n1:1", Direction = ScrollDirection.Down },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ANodeCenterOutsideTheWindowReturnsStaleSnapshotAndSendsNoInput()
    {
        var target = List() with { Bounds = new Rect(5000, 5000, 120, 32) };
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ScrollRequest { NodeId = "n1:1", Direction = ScrollDirection.Down },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task ScrollAtANodeUsesTheLiveBoundsCenter()
    {
        // The live element reports new bounds after the snapshot; the wheel must
        // land on where the element is now, not where it was captured.
        var target = List();
        var live = FakeUiaNode.From(target) with { Bounds = new Rect(100, 200, 40, 40) };
        var harness = Harness.Create(new FixedNodeResolver(live));
        harness.SaveNode("n1:1", target);

        await harness.Action.ExecuteAsync(
            new ScrollRequest { NodeId = "n1:1", Direction = ScrollDirection.Down },
            CancellationToken.None);

        Assert.Equal([(120, 220)], harness.Input.Moves);
        Assert.Equal([(-120, false)], harness.Input.Scrolls);
    }

    [Fact]
    public async Task ScrollUpOneAtImageCoordinatesMapsThroughTheSnapshot()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.SaveSnapshot(generation: 1, image: true, scale: 1.5);

        // Space and amount are omitted: the host defaults are image space and 1.
        var result = await harness.Action.ExecuteAsync(
            new ScrollRequest { X = 10, Y = 20, Direction = ScrollDirection.Up },
            CancellationToken.None);

        Assert.Equal("wheel", result.Method);
        Assert.Equal([(115, 80)], harness.Input.Moves);
        Assert.Equal([(120, false)], harness.Input.Scrolls);
        Assert.Equal(1, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task ScrollLeftTwoSendsAHorizontalMinus240()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        await harness.Action.ExecuteAsync(
            new ScrollRequest
            {
                X = 300,
                Y = 200,
                Space = CoordinateSpace.Screen,
                Direction = ScrollDirection.Left,
                Amount = 2,
            },
            CancellationToken.None);

        Assert.Equal([(300, 200)], harness.Input.Moves);
        Assert.Equal([(-240, true)], harness.Input.Scrolls);
    }

    [Fact]
    public async Task ScrollRightSendsAPositiveHorizontalDelta()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        await harness.Action.ExecuteAsync(
            new ScrollRequest
            {
                X = 300,
                Y = 200,
                Space = CoordinateSpace.Screen,
                Direction = ScrollDirection.Right,
                Amount = 2,
            },
            CancellationToken.None);

        Assert.Equal([(240, true)], harness.Input.Scrolls);
    }

    [Theory]
    [InlineData(0, -120)]
    [InlineData(-5, -120)]
    [InlineData(101, -12000)]
    public async Task TheAmountIsClampedToOneToHundred(int amount, int delta)
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        await harness.Action.ExecuteAsync(
            new ScrollRequest
            {
                X = 300,
                Y = 200,
                Space = CoordinateSpace.Screen,
                Direction = ScrollDirection.Down,
                Amount = amount,
            },
            CancellationToken.None);

        Assert.Equal([(delta, false)], harness.Input.Scrolls);
    }

    [Fact]
    public async Task AMovedWindowWithImageSpaceCoordinatesReturnsStaleSnapshot()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));
        harness.SaveSnapshot(generation: 1, image: true, scale: 1.5);
        harness.MoveWindow(new Rect(200, 100, 800, 600));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ScrollRequest
                {
                    X = 10,
                    Y = 20,
                    Space = CoordinateSpace.Image,
                    Direction = ScrollDirection.Down,
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task AFailedExpectationSendsNoScroll()
    {
        var walker = new FakeUiaWalker(new TreeModel { Role = "window", Name = "Notepad" });
        var harness = Harness.Create(new FakeUiaNodeResolver(null), walker: walker);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ScrollRequest
                {
                    X = 300,
                    Y = 200,
                    Space = CoordinateSpace.Screen,
                    Direction = ScrollDirection.Down,
                    Expect = [new ExpectNode { Name = "Missing" }],
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.ExpectationFailed, failure.Code);
        Assert.Equal("node:Missing", failure.FailedPredicate);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task AStaleNodeGenerationReturnsStaleSnapshotAndSendsNoInput()
    {
        var target = List();
        var harness = Harness.Create(new FakeUiaNodeResolver(FakeUiaNode.From(target)));
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ScrollRequest { NodeId = "n2:1", Direction = ScrollDirection.Down },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task AFocusRefusalSendsNoScroll()
    {
        var target = List();
        var harness = Harness.Create(
            new FakeUiaNodeResolver(FakeUiaNode.From(target)),
            foregroundSucceeds: false);
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ScrollRequest { NodeId = "n1:1", Direction = ScrollDirection.Down },
                CancellationToken.None));

        Assert.Equal(ErrorCode.FocusRefused, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task BothANodeAndCoordinatesIsInvalid()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ScrollRequest
                {
                    NodeId = "n1:1",
                    X = 10,
                    Y = 20,
                    Direction = ScrollDirection.Down,
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task AScrollWithoutATargetIsInvalid()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ScrollRequest { Direction = ScrollDirection.Down },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task AnUnknownDirectionIsInvalidAndSendsNoInput()
    {
        var harness = Harness.Create(new FakeUiaNodeResolver(null));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ScrollRequest
                {
                    X = 300,
                    Y = 200,
                    Space = CoordinateSpace.Screen,
                    Direction = (ScrollDirection)99,
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task HonorsCancellationBeforeInput()
    {
        var target = List();
        var harness = Harness.Create(new FakeUiaNodeResolver(FakeUiaNode.From(target)));
        harness.SaveNode("n1:1", target);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Action.ExecuteAsync(
                new ScrollRequest { NodeId = "n1:1", Direction = ScrollDirection.Down },
                cancellation.Token));

        Assert.Empty(harness.Input.Calls);
    }

    private static TreeModel List() => new()
    {
        Role = "list",
        Name = "Messages",
        Identifier = "messages",
        Bounds = new Rect(110, 70, 120, 32),
    };

    /// <summary>
    /// Returns its node regardless of the fingerprint, so a test can prove the
    /// action reads the live bounds rather than the captured ones.
    /// </summary>
    private sealed class FixedNodeResolver : IUiaNodeResolver
    {
        private readonly IUiaNode _node;

        public FixedNodeResolver(IUiaNode node) => _node = node;

        public IUiaNode? Resolve(nint hwnd, TreeModel target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _node;
        }
    }

    /// <summary>
    /// One selected window plus every scroll dependency, wired exactly as the
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

            Action = new ScrollAction(
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

        public ScrollAction Action { get; }

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
