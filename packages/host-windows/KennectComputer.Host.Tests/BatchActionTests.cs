using KennectComputer.Host.Actions;
using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// Pins the ordered batch flow (DESIGN §6.11): steps run serially in the order
/// given, the first action failure stops the batch and reports its zero-based
/// index with per-step results, optional before/after snapshots are
/// accessibility-only and best-effort, and a stop token between steps returns
/// the completed steps instead of inventing a step failure. The real action
/// services are wired to the same fakes the action tests use, so no desktop,
/// window, input, or clipboard is touched.
/// </summary>
public sealed class BatchActionTests
{
    private static readonly Rect WindowBounds = new(100, 50, 800, 600);

    [Fact]
    public async Task ThreeSuccessfulStepsRunInOrder()
    {
        var harness = Harness.Create();

        var result = await harness.Action.ExecuteAsync(
            new BatchRequest
            {
                Steps =
                [
                    new ClickStep
                    {
                        Request = new ClickRequest
                        {
                            X = 300,
                            Y = 200,
                            Space = CoordinateSpace.Screen,
                        },
                    },
                    new KeyStep { Request = new KeyRequest { Keys = "Enter" } },
                    new ScrollStep
                    {
                        Request = new ScrollRequest
                        {
                            X = 300,
                            Y = 200,
                            Space = CoordinateSpace.Screen,
                            Direction = ScrollDirection.Down,
                            Amount = 2,
                        },
                    },
                ],
            },
            CancellationToken.None);

        Assert.Null(result.FailedIndex);
        Assert.Null(result.SnapshotBefore);
        Assert.Null(result.SnapshotAfter);
        Assert.Null(result.Warning);
        Assert.Equal(3, result.Steps.Count);

        Assert.Equal(0, result.Steps[0].Index);
        Assert.Equal("click", result.Steps[0].Kind);
        Assert.True(result.Steps[0].Ok);
        Assert.Null(result.Steps[0].Code);
        Assert.Null(result.Steps[0].Message);
        Assert.Equal("click", Assert.IsType<ActionResult>(result.Steps[0].Data).Action);

        Assert.Equal(1, result.Steps[1].Index);
        Assert.Equal("key", result.Steps[1].Kind);
        Assert.True(result.Steps[1].Ok);
        Assert.Equal("key", Assert.IsType<ActionResult>(result.Steps[1].Data).Action);

        Assert.Equal(2, result.Steps[2].Index);
        Assert.Equal("scroll", result.Steps[2].Kind);
        Assert.True(result.Steps[2].Ok);
        Assert.Equal("scroll", Assert.IsType<ActionResult>(result.Steps[2].Data).Action);

        // Input order is the execution order: one step's events complete
        // before the next step starts.
        Assert.Equal(
            [
                "MoveTo:300,200",
                "Click:Left:1",
                "KeyDown:13",
                "KeyUp:13",
                "MoveTo:300,200",
                "Scroll:-240:False",
            ],
            harness.Input.Calls);
    }

    [Fact]
    public async Task TheSecondStepFailsAndTheThirdIsNotRun()
    {
        var harness = Harness.Create();

        var result = await harness.Action.ExecuteAsync(
            new BatchRequest
            {
                Steps =
                [
                    new WaitStep
                    {
                        Request = new WaitRequest
                        {
                            Condition = new WaitWindowCondition { Process = "Notepad" },
                        },
                    },
                    new TypeStep { Request = new TypeRequest { Text = string.Empty } },
                    new ClickStep
                    {
                        Request = new ClickRequest
                        {
                            X = 300,
                            Y = 200,
                            Space = CoordinateSpace.Screen,
                        },
                    },
                ],
            },
            CancellationToken.None);

        Assert.Equal(1, result.FailedIndex);
        Assert.Equal(2, result.Steps.Count);

        Assert.Equal("wait", result.Steps[0].Kind);
        Assert.True(result.Steps[0].Ok);
        Assert.IsType<WaitResult>(result.Steps[0].Data);

        Assert.Equal(1, result.Steps[1].Index);
        Assert.Equal("type", result.Steps[1].Kind);
        Assert.False(result.Steps[1].Ok);
        Assert.Equal(ErrorCode.InvalidRequest, result.Steps[1].Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Steps[1].Message));
        Assert.Null(result.Steps[1].Data);

        // The third step never ran: it would have moved the pointer and clicked.
        Assert.Empty(harness.Input.Calls);
        Assert.Empty(harness.Clipboard.Sets);
    }

    [Fact]
    public async Task SnapshotsAreAccessibilityOnlyAndSavedToTheStore()
    {
        var harness = Harness.Create();

        var result = await harness.Action.ExecuteAsync(
            new BatchRequest
            {
                Steps = [new KeyStep { Request = new KeyRequest { Keys = "Enter" } }],
                SnapshotBefore = true,
                SnapshotAfter = true,
            },
            CancellationToken.None);

        Assert.Null(result.FailedIndex);

        var before = Assert.IsType<Snapshot>(result.SnapshotBefore);
        var after = Assert.IsType<Snapshot>(result.SnapshotAfter);

        Assert.NotEmpty(before.Nodes);
        Assert.NotEmpty(after.Nodes);
        Assert.True(after.Generation > before.Generation);
        Assert.Equal(harness.SelectedWindow.WindowId, before.Window.WindowId);
        Assert.Equal(harness.SelectedWindow.WindowId, after.Window.WindowId);

        // Accessibility-only: no image was captured or attached.
        Assert.Null(before.Image);
        Assert.Null(after.Image);
        Assert.Empty(harness.Capture.Calls);
        var walker = Assert.IsType<FakeUiaWalker>(harness.Walker);
        Assert.Equal(SnapshotMode.A11y, walker.LastOptions?.Mode);
        Assert.False(walker.LastOptions?.Image ?? true);

        // The after snapshot is what later actions will resolve node IDs against.
        Assert.True(harness.Snapshots.TryGetState(harness.SelectedWindow.WindowId, out var state));
        Assert.NotNull(state);
        Assert.Equal(after.Generation, state.Generation);
        Assert.NotEmpty(state.Nodes);
    }

    [Fact]
    public async Task SnapshotBeforeIsReturnedWithoutReplacingTheStoredSnapshot()
    {
        // The batch's steps act on the state an earlier snapshot produced: a
        // node ID and an image-space coordinate. A before snapshot is a
        // report, not a new target state, so it must not invalidate either.
        var target = new TreeModel
        {
            Role = "button",
            Name = "Send",
            Bounds = new Rect(110, 70, 120, 32),
            Actions = ["press"],
        };
        var live = FakeUiaNode.From(target);
        var harness = Harness.Create(liveNode: live);
        harness.SaveSnapshotWithImage("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new BatchRequest
            {
                Steps =
                [
                    new ClickStep { Request = new ClickRequest { NodeId = "n1:1" } },
                    new ClickStep { Request = new ClickRequest { X = 10, Y = 20 } },
                ],
                SnapshotBefore = true,
            },
            CancellationToken.None);

        Assert.Null(result.FailedIndex);
        Assert.Equal(2, result.Steps.Count);
        Assert.All(result.Steps, step => Assert.True(step.Ok));
        Assert.NotNull(result.SnapshotBefore);

        // Both steps ran, so the before snapshot cannot have replaced the
        // generation or dropped the image the steps depended on.
        Assert.True(harness.Snapshots.TryGetState(harness.SelectedWindow.WindowId, out var state));
        Assert.NotNull(state);
        Assert.Equal(1, state.Generation);
        Assert.NotNull(state.Image);
        Assert.True(harness.Snapshots.TryGetNode(harness.SelectedWindow.WindowId, "n1:1", out _));
    }

    [Fact]
    public async Task AStopTokenBetweenStepsStopsTheBatchAndReportsCompletedSteps()
    {
        var harness = Harness.Create();
        using var cancellation = new CancellationTokenSource();

        // The key completes, then the token is cancelled: the next step must
        // not run, and the completed step must still be reported.
        harness.Input.BeforeKeyDown = cancellation.Cancel;

        var result = await harness.Action.ExecuteAsync(
            new BatchRequest
            {
                Steps =
                [
                    new KeyStep { Request = new KeyRequest { Keys = "Enter" } },
                    new ClickStep
                    {
                        Request = new ClickRequest
                        {
                            X = 300,
                            Y = 200,
                            Space = CoordinateSpace.Screen,
                        },
                    },
                ],
            },
            cancellation.Token);

        var completed = Assert.Single(result.Steps);
        Assert.Equal(0, completed.Index);
        Assert.True(completed.Ok);
        Assert.Equal(1, result.FailedIndex);
        Assert.Equal(["KeyDown:13", "KeyUp:13"], harness.Input.Calls);
    }

    [Fact]
    public async Task ACancelledWaitPropagatesInsteadOfBecomingAStepFailure()
    {
        var harness = Harness.Create();
        using var cancellation = new CancellationTokenSource();
        harness.Clock.OnDelay = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Action.ExecuteAsync(
                new BatchRequest
                {
                    Steps =
                    [
                        new WaitStep
                        {
                            Request = new WaitRequest
                            {
                                Condition = new WaitWindowCondition { Process = "Missing" },
                                TimeoutMs = 60_000,
                            },
                        },
                    ],
                },
                cancellation.Token));
    }

    [Fact]
    public async Task AnEmptyStepListIsInvalid()
    {
        var harness = Harness.Create();

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(new BatchRequest(), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task AFailedSnapshotIsRecordedAsAWarningAndTheStepsStillRun()
    {
        // No selected window: the before snapshot cannot resolve a target, but
        // a window wait condition needs no selection and must still run.
        var harness = Harness.Create(select: false);

        var result = await harness.Action.ExecuteAsync(
            new BatchRequest
            {
                Steps =
                [
                    new WaitStep
                    {
                        Request = new WaitRequest
                        {
                            Condition = new WaitWindowCondition { Process = "Notepad" },
                        },
                    },
                ],
                SnapshotBefore = true,
            },
            CancellationToken.None);

        Assert.Null(result.SnapshotBefore);
        Assert.Null(result.FailedIndex);
        Assert.True(Assert.Single(result.Steps).Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.Warning));
        Assert.Contains("No window is selected", Assert.IsType<string>(result.Warning));
    }

    [Fact]
    public async Task AnUnexpectedSnapshotFailureIsAlsoOnlyAWarning()
    {
        var harness = Harness.Create(walker: new ThrowingWalker());

        var result = await harness.Action.ExecuteAsync(
            new BatchRequest
            {
                Steps =
                [
                    new WaitStep
                    {
                        Request = new WaitRequest
                        {
                            Condition = new WaitWindowCondition { Process = "Notepad" },
                        },
                    },
                ],
                SnapshotBefore = true,
            },
            CancellationToken.None);

        Assert.Null(result.SnapshotBefore);
        Assert.True(Assert.Single(result.Steps).Ok);
        Assert.Contains("uia provider unavailable", Assert.IsType<string>(result.Warning));
    }

    [Fact]
    public async Task APrecancelledTokenRunsNoStepsAndNamesTheFirstStep()
    {
        var harness = Harness.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await harness.Action.ExecuteAsync(
            new BatchRequest
            {
                Steps = [new KeyStep { Request = new KeyRequest { Keys = "Enter" } }],
            },
            cancellation.Token);

        Assert.Empty(result.Steps);
        Assert.Equal(0, result.FailedIndex);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task AReadTextStepReturnsItsResultAsData()
    {
        var target = new TreeModel
        {
            Role = "edit",
            Name = "Message",
            Bounds = new Rect(10, 20, 200, 40),
        };
        var live = FakeUiaNode.From(target) with { Text = "Hello batch" };
        var harness = Harness.Create(liveNode: live);
        harness.SaveNode("n1:4", target);

        var result = await harness.Action.ExecuteAsync(
            new BatchRequest
            {
                Steps =
                [
                    new ReadTextStep
                    {
                        Request = new ReadTextRequest { NodeId = "n1:4" },
                    },
                ],
            },
            CancellationToken.None);

        var step = Assert.Single(result.Steps);
        Assert.Equal("readText", step.Kind);
        Assert.True(step.Ok);

        var data = Assert.IsType<ReadTextResult>(step.Data);
        Assert.Equal("n1:4", data.NodeId);
        Assert.Equal("Hello batch", data.Text);
    }

    /// <summary>
    /// Every batch dependency, wired exactly as the dispatcher will wire them.
    /// Fakes stand in for the UIA walker, foreground API, input sender,
    /// clipboard, window API, and clock, so batches never touch the desktop.
    /// </summary>
    private sealed class Harness
    {
        private Harness(bool select, IUiaNode? liveNode, IUiaWalker? walker)
        {
            Raw = new RawWindow(1, 100, "Notepad", "Notepad", WindowBounds)
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
            Foreground = new FakeWin32 { InitialForeground = 99 };
            Walker = walker ?? new FakeUiaWalker();
            Capture = new FakeWindowCapture();
            Clock = new BatchClock();
            Snapshotter = new UiaSnapshotter(
                Walker,
                _ => { },
                _ => { },
                Capture.Capture);

            var preflight = new ActionPreflight(
                new ForegroundService(Foreground),
                Selection,
                Registry,
                new ExpectEvaluator(Walker, Foreground),
                TimeSpan.FromMilliseconds(10));

            var resolver = new FakeUiaNodeResolver(liveNode);

            Action = new BatchAction(
                new ClickAction(Walker, resolver, preflight, Input, Snapshots),
                new TypeAction(resolver, preflight, Input, Clipboard, Snapshots),
                new KeyAction(preflight, Input),
                new ScrollAction(resolver, preflight, Input, Snapshots),
                new WaitAction(
                    Walker,
                    new WindowEnumerator(Windows, Registry),
                    Selection,
                    Registry,
                    Clock),
                new ReadTextAction(resolver, Snapshots, Selection, Registry),
                Snapshotter,
                Snapshots,
                Selection,
                Registry);
        }

        public RawWindow Raw { get; }

        public FakeWindowApi Windows { get; }

        public WindowRegistry Registry { get; }

        public WindowInfo SelectedWindow { get; }

        public SelectionStore Selection { get; }

        public SnapshotStore Snapshots { get; }

        public FakeInputSender Input { get; }

        public FakeClipboard Clipboard { get; }

        public FakeWin32 Foreground { get; }

        public IUiaWalker Walker { get; }

        public FakeWindowCapture Capture { get; }

        public BatchClock Clock { get; }

        public UiaSnapshotter Snapshotter { get; }

        public BatchAction Action { get; }

        public static Harness Create(
            bool select = true,
            IUiaNode? liveNode = null,
            IUiaWalker? walker = null)
            => new(select, liveNode, walker);

        /// <summary>Stores a snapshot generation with one addressable node.</summary>
        public void SaveNode(string nodeId, TreeModel node, int generation = 1)
        {
            var nodes = new Dictionary<string, TreeModel>(StringComparer.Ordinal)
            {
                [nodeId] = node,
            };

            Snapshots.Save(
                SelectedWindow.WindowId,
                new Snapshot { Generation = generation },
                nodes,
                SelectedWindow.Bounds);
        }

        /// <summary>
        /// Stores an actionable generation that also has image metadata, so a
        /// batch step can use image-space coordinates.
        /// </summary>
        public void SaveSnapshotWithImage(string nodeId, TreeModel node, int generation = 1)
        {
            Snapshots.Save(
                SelectedWindow.WindowId,
                new Snapshot
                {
                    Generation = generation,
                    Image = new ImagePayload
                    {
                        Data = "x",
                        MimeType = "image/png",
                        Scale = 1.5,
                        SourceBounds = SelectedWindow.Bounds,
                    },
                },
                new Dictionary<string, TreeModel>(StringComparer.Ordinal)
                {
                    [nodeId] = node,
                },
                SelectedWindow.Bounds);
        }
    }

    /// <summary>Models a UIA provider that fails mid-read, for warning tests.</summary>
    private sealed class ThrowingWalker : IUiaWalker
    {
        public TreeModel? Walk(
            nint hwnd,
            SnapshotOptions options,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("uia provider unavailable");
    }

    /// <summary>
    /// A clock that never sleeps: each delay advances fake time and may run a
    /// hook, so a test can stop a waiting batch between polls.
    /// </summary>
    private sealed class BatchClock : IWaitClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public Action<int>? OnDelay { get; set; }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
            OnDelay?.Invoke(1);
            return Task.CompletedTask;
        }
    }
}
