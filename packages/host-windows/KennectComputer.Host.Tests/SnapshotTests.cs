using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;

namespace KennectComputer.Host.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void RetriesWhenTheFirstTreeIsEmpty()
    {
        var walker = new FakeUiaWalker(emptyFirst: true);

        var snapshot = NewSnapshotter(walker).Snapshot(1, DefaultBounds, new SnapshotOptions(), CancellationToken.None);

        Assert.Equal(2, walker.Calls);
        Assert.Null(snapshot.Warning);
        Assert.NotEmpty(snapshot.Nodes);
    }

    [Fact]
    public void StopsAfterBoundedWarmupAttemptsAndReturnsTheWarning()
    {
        // R9: Chromium accessibility is cold. The snapshotter retries three times
        // with a delay between attempts, then degrades to a warning instead of
        // hanging or raising. Both the focus and the wait are injected so this
        // test never touches the desktop or sleeps.
        var walker = new FakeUiaWalker(alwaysEmpty: true);
        var waits = new List<int>();
        var focuses = new List<int>();

        var snapshot = NewSnapshotter(walker, waits, focuses)
            .Snapshot((nint)42, DefaultBounds, new SnapshotOptions(), CancellationToken.None);

        Assert.Equal(4, walker.Calls);
        Assert.Equal(3, waits.Count);
        Assert.Equal([42, 42, 42], focuses);
        Assert.Equal("accessibility tree empty; vision fallback required", snapshot.Warning);
        Assert.Empty(snapshot.Nodes);
        Assert.Equal(0, snapshot.TotalNodes);
    }

    [Fact]
    public void DoesNotRetryWhenTheFirstWalkAlreadyHasNodes()
    {
        var walker = new FakeUiaWalker();
        var waits = new List<int>();

        var snapshot = NewSnapshotter(walker, waits).Snapshot(1, DefaultBounds, new SnapshotOptions(), CancellationToken.None);

        Assert.Equal(1, walker.Calls);
        Assert.Empty(waits);
        Assert.Null(snapshot.Warning);
    }

    [Fact]
    public void VisionModeSkipsTheWalk()
    {
        var walker = new FakeUiaWalker(alwaysEmpty: true);

        var snapshot = NewSnapshotter(walker)
            .Snapshot(1, DefaultBounds, new SnapshotOptions { Mode = SnapshotMode.Vision }, CancellationToken.None);

        Assert.Equal(0, walker.Calls);
        Assert.Empty(snapshot.Nodes);
        Assert.Equal(0, snapshot.TotalNodes);
        Assert.False(snapshot.Truncated);
        Assert.Null(snapshot.Warning);
    }

    [Fact]
    public void VisionModeStillCapturesTheRequestedImage()
    {
        // Vision mode skips the accessibility walk, but the image is the whole
        // point of the mode, so the capture seam must still run.
        var payload = SampleImagePayload();
        var capture = new FakeWindowCapture(payload);
        var walker = new FakeUiaWalker(alwaysEmpty: true);

        var snapshot = NewSnapshotter(walker, capture: capture)
            .Snapshot(
                1,
                DefaultBounds,
                new SnapshotOptions { Mode = SnapshotMode.Vision, Image = true, ImageLongEdge = 1024 },
                CancellationToken.None);

        Assert.Equal(0, walker.Calls);
        Assert.Same(payload, snapshot.Image);
        var call = Assert.Single(capture.Calls);
        Assert.Equal(1024, call.ImageLongEdge);
        Assert.True(call.Enabled);
    }

    [Fact]
    public void AttachesTheRequestedImageToTheSnapshot()
    {
        var payload = SampleImagePayload();
        var capture = new FakeWindowCapture(payload);

        var snapshot = NewSnapshotter(new FakeUiaWalker(), capture: capture)
            .Snapshot(
                (nint)42,
                DefaultBounds,
                new SnapshotOptions { Image = true, ImageLongEdge = 1024 },
                CancellationToken.None);

        Assert.Same(payload, snapshot.Image);
        Assert.NotEmpty(snapshot.Nodes);
        var call = Assert.Single(capture.Calls);
        Assert.Equal((nint)42, call.Hwnd);
        Assert.Same(DefaultBounds, call.Bounds);
        Assert.Equal(1024, call.ImageLongEdge);
        Assert.True(call.Enabled);
    }

    [Fact]
    public void OmitsTheImageWhenTheCallerDidNotRequestIt()
    {
        var capture = new FakeWindowCapture(SampleImagePayload());

        var snapshot = NewSnapshotter(new FakeUiaWalker(), capture: capture)
            .Snapshot(1, DefaultBounds, new SnapshotOptions(), CancellationToken.None);

        Assert.Empty(capture.Calls);
        Assert.Null(snapshot.Image);
    }

    [Fact]
    public void KeepsTheSnapshotWhenTheImageCaptureFails()
    {
        // ScreenCapture.Capture returns null on failure instead of throwing;
        // a failed image must never fail the accessibility snapshot or add a
        // warning.
        var capture = new FakeWindowCapture(payload: null);

        var snapshot = NewSnapshotter(new FakeUiaWalker(), capture: capture)
            .Snapshot(1, DefaultBounds, new SnapshotOptions { Image = true }, CancellationToken.None);

        Assert.Single(capture.Calls);
        Assert.Null(snapshot.Image);
        Assert.Null(snapshot.Warning);
        Assert.NotEmpty(snapshot.Nodes);
    }

    [Fact]
    public void TheEmptyTreeWarningStillCarriesTheRequestedImage()
    {
        // An empty accessibility tree is the vision-fallback case; the image is
        // the fallback, so it must still be attached to the warning snapshot.
        var payload = SampleImagePayload();
        var capture = new FakeWindowCapture(payload);

        var snapshot = NewSnapshotter(
                new FakeUiaWalker(alwaysEmpty: true),
                waits: [],
                focuses: [],
                capture: capture)
            .Snapshot(1, DefaultBounds, new SnapshotOptions { Image = true }, CancellationToken.None);

        Assert.Same(payload, snapshot.Image);
        Assert.Equal("accessibility tree empty; vision fallback required", snapshot.Warning);
        Assert.Empty(snapshot.Nodes);
    }

    [Fact]
    public void GenerationIncrementsOnEverySnapshot()
    {
        var snapshotter = NewSnapshotter(new FakeUiaWalker());

        var first = snapshotter.Snapshot(1, DefaultBounds, new SnapshotOptions(), CancellationToken.None);
        var second = snapshotter.Snapshot(1, DefaultBounds, new SnapshotOptions(), CancellationToken.None);

        Assert.Equal(1, first.Generation);
        Assert.Equal(2, second.Generation);
        Assert.All(first.Nodes, node => Assert.StartsWith("n1:", node.NodeId));
        Assert.All(second.Nodes, node => Assert.StartsWith("n2:", node.NodeId));
    }

    [Fact]
    public void ReportsFocusedNodeIdAndModalWindows()
    {
        var tree = new TreeModel
        {
            Role = "window",
            Name = "Host",
            Children =
            [
                new TreeModel { Role = "button", Name = "Discard", Focused = true, Actions = ["press"] },
                new TreeModel
                {
                    Role = "group",
                    Name = "Unsaved changes",
                    IsModal = true,
                    Children =
                    [
                        new TreeModel { Role = "button", Name = "Save", Actions = ["press"] },
                    ],
                },
            ],
        };

        var snapshot = new UiaSnapshotter(new FakeUiaWalker(tree), _ => { }, _ => { })
            .Snapshot(1, DefaultBounds, new SnapshotOptions(), CancellationToken.None);

        Assert.True(snapshot.Modal);
        Assert.Equal("n1:1", snapshot.FocusedNodeId);
        Assert.True(snapshot.Nodes[0].Focused);
    }

    [Fact]
    public void HiddenModalElementsDoNotMarkTheSnapshotModal()
    {
        var tree = new TreeModel
        {
            Role = "window",
            Name = "Host",
            Children =
            [
                new TreeModel { Role = "group", Name = "Hidden dialog", IsModal = true, Offscreen = true },
            ],
        };

        var snapshot = new UiaSnapshotter(new FakeUiaWalker(tree), _ => { }, _ => { })
            .Snapshot(1, DefaultBounds, new SnapshotOptions(), CancellationToken.None);

        Assert.False(snapshot.Modal);
        Assert.Empty(snapshot.Nodes);
        Assert.Equal("accessibility tree empty; vision fallback required", snapshot.Warning);
    }

    [Fact]
    public void CaptureExposesOnlyShownNodesInTheNodeMap()
    {
        var shown = new TreeModel { Role = "button", Name = "Shown", Actions = ["press"] };
        var hidden = new TreeModel { Role = "button", Name = "Hidden", Actions = ["press"], Offscreen = true };
        var tree = new TreeModel { Role = "window", Children = [shown, hidden] };

        var capture = new UiaSnapshotter(new FakeUiaWalker(tree), _ => { }, _ => { })
            .Capture(1, DefaultBounds, new SnapshotOptions(), CancellationToken.None);

        var node = Assert.Single(capture.Snapshot.Nodes);
        Assert.Equal("n1:1", node.NodeId);
        Assert.Same(shown, capture.NodeMap[node.NodeId]);
        Assert.Single(capture.NodeMap);
    }

    [Fact]
    public void SnapshotStoreRejectsNodeIdsFromOlderGenerations()
    {
        var store = new SnapshotStore();
        var firstSource = new TreeModel { Role = "button", Name = "First" };
        var secondSource = new TreeModel { Role = "button", Name = "Second" };

        store.Save(
            "w_1",
            new Snapshot { Generation = 1, Nodes = [] },
            new Dictionary<string, TreeModel> { [NodeId.Create(1, 1)] = firstSource });
        Assert.True(store.TryGetNode("w_1", NodeId.Create(1, 1), out var first));
        Assert.Same(firstSource, first);

        store.Save(
            "w_1",
            new Snapshot { Generation = 2, Nodes = [] },
            new Dictionary<string, TreeModel> { [NodeId.Create(2, 1)] = secondSource });

        Assert.False(store.TryGetNode("w_1", NodeId.Create(1, 1), out _));
        Assert.True(store.TryGetNode("w_1", NodeId.Create(2, 1), out var second));
        Assert.Same(secondSource, second);
        Assert.False(store.TryGetNode("w_2", NodeId.Create(2, 1), out _));
        Assert.False(store.TryGetNode("w_1", "not-a-node-id", out _));
    }

    [Fact]
    public void SnapshotStoreToleratesConcurrentSavesAndReads()
    {
        var store = new SnapshotStore();

        Parallel.For(0, 200, index =>
        {
            var nodeId = NodeId.Create(index + 1, 1);
            store.Save(
                "w_1",
                new Snapshot { Generation = index + 1, Nodes = [] },
                new Dictionary<string, TreeModel>
                {
                    [nodeId] = new TreeModel { Role = "button", Name = $"node{index}" },
                });
            store.TryGetNode("w_1", nodeId, out _);
        });

        store.Save(
            "w_final",
            new Snapshot { Generation = 1, Nodes = [] },
            new Dictionary<string, TreeModel>
            {
                [NodeId.Create(1, 1)] = new TreeModel { Role = "button", Name = "Final" },
            });
        Assert.True(store.TryGetNode("w_final", NodeId.Create(1, 1), out _));
    }

    [Fact]
    public void SnapshotsAnOwnedWinFormsWindow()
    {
        // Integration without touching the user's desktop: the test owns this
        // window and closes it. It deliberately avoids launching a real app, so
        // the normal suite cannot leave Notepad (or any other app) behind.
        using var window = OwnedTestWindow.Start();

        var snapshot = new UiaSnapshotter(new WindowsUiaWalker(), _ => { }, _ => { })
            .Snapshot(
                window.Handle,
                window.Bounds,
                new SnapshotOptions { Mode = SnapshotMode.A11y },
                CancellationToken.None);

        Assert.Null(snapshot.Warning);
        Assert.False(snapshot.Modal);
        Assert.True(
            snapshot.Nodes.Count >= 10,
            $"Expected at least 10 nodes from the test window, got {snapshot.Nodes.Count}.");
        Assert.Equal(
            snapshot.Nodes.Count,
            snapshot.Nodes.Select(node => node.NodeId).Distinct().Count());
        Assert.All(
            snapshot.Nodes,
            node => Assert.StartsWith($"n{snapshot.Generation}:", node.NodeId));
        Assert.Contains(
            snapshot.Nodes,
            node => node.Role == "button"
                && node.Name == "Action 0"
                && node.Actions.Contains("press"));
        Assert.Contains(
            snapshot.Nodes,
            node => node.Role == "edit" && node.Name == "Editor");

        // The window is deliberately shown without activation, so a focused
        // node is not guaranteed; when UIA reports one it must be a shown node
        // with the matching focused flag.
        if (snapshot.FocusedNodeId is { } focusedNodeId)
        {
            Assert.Contains(
                snapshot.Nodes,
                node => node.NodeId == focusedNodeId && node.Focused);
        }
    }

    private static readonly Rect DefaultBounds = new(0, 0, 800, 600);

    private static ImagePayload SampleImagePayload() => SampleImages.Payload();

    private static UiaSnapshotter NewSnapshotter(
        IUiaWalker walker,
        List<int>? waits = null,
        List<int>? focuses = null,
        FakeWindowCapture? capture = null)
    {
        WindowCapture? captureImage = capture is null ? null : capture.Capture;
        return new(
            walker,
            hwnd => focuses?.Add((int)hwnd),
            _ => waits?.Add(1),
            captureImage);
    }
}

/// <summary>Records the snapshotter's image captures and returns a fixed payload.</summary>
internal sealed class FakeWindowCapture
{
    private readonly ImagePayload? _payload;

    public FakeWindowCapture(ImagePayload? payload = null)
    {
        _payload = payload;
    }

    public List<CaptureCall> Calls { get; } = [];

    public ImagePayload? Capture(nint hwnd, Rect bounds, int imageLongEdge, bool enabled)
    {
        Calls.Add(new CaptureCall(hwnd, bounds, imageLongEdge, enabled));
        return enabled ? _payload : null;
    }
}

internal sealed record CaptureCall(nint Hwnd, Rect Bounds, int ImageLongEdge, bool Enabled);

internal static class SampleImages
{
    /// <summary>A protocol-shaped payload; unit tests never decode its data.</summary>
    public static ImagePayload Payload() => new()
    {
        Data = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
        MimeType = "image/png",
        Scale = 1.5,
        SourceBounds = new Rect(0, 0, 800, 600),
    };
}

/// <summary>
/// A WinForms window created on an STA thread that the test owns and closes.
/// It is shown without activation so a test run never steals the user's
/// foreground. The controls set <c>AccessibleName</c> so the UIA names are
/// deterministic.
/// </summary>
internal sealed class OwnedTestWindow : IDisposable
{
    private readonly Form _form;
    private readonly Thread _thread;

    private OwnedTestWindow(Form form, Thread thread, nint handle, Rect bounds)
    {
        _form = form;
        _thread = thread;
        Handle = handle;
        Bounds = bounds;
    }

    public nint Handle { get; }

    /// <summary>The window's outer bounds in screen pixels, as reported by WinForms.</summary>
    public Rect Bounds { get; }

    public static OwnedTestWindow Start()
    {
        var ready = new ManualResetEventSlim(initialState: false);
        Form? form = null;
        nint handle = 0;
        var bounds = new Rect();

        var thread = new Thread(() =>
        {
            form = new SnapshotTestForm
            {
                Text = "Kennect Snapshot Test",
                StartPosition = FormStartPosition.Manual,
                Location = new Point(20, 20),
                Size = new Size(640, 480),
                ShowInTaskbar = false,
            };

            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
            };

            for (var index = 0; index < 12; index++)
            {
                panel.Controls.Add(new Button
                {
                    AccessibleName = $"Action {index}",
                    Text = $"Action {index}",
                    Width = 160,
                    Height = 28,
                });
            }

            panel.Controls.Add(new TextBox
            {
                AccessibleName = "Editor",
                Text = "Text",
                Width = 200,
            });
            panel.Controls.Add(new Label
            {
                AccessibleName = "Caption",
                Text = "Caption",
                AutoSize = true,
            });
            form.Controls.Add(panel);

            form.Shown += (_, _) =>
            {
                handle = form.Handle;
                bounds = new Rect(
                    form.Bounds.X,
                    form.Bounds.Y,
                    form.Bounds.Width,
                    form.Bounds.Height);
                ready.Set();
            };

            Application.Run(form);
        })
        {
            IsBackground = true,
            Name = "kennect-snapshot-test-window",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("The test-owned window did not appear.");
        }

        return new OwnedTestWindow(form!, thread, handle, bounds);
    }

    public void Dispose()
    {
        try
        {
            if (_form.IsHandleCreated && !_form.IsDisposed)
            {
                _form.Invoke(new Action(_form.Close));
            }
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException derives from this; either way the message
            // loop already ended and there is nothing to close.
        }

        _thread.Join(TimeSpan.FromSeconds(10));
    }

    private sealed class SnapshotTestForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}

/// <summary>
/// A deterministic walker for snapshot tests. <c>emptyFirst</c> models a cold
/// Chromium tree that populates on the second walk; <c>alwaysEmpty</c> models
/// an app that exposes no accessibility content at all.
/// </summary>
internal sealed class FakeUiaWalker : IUiaWalker
{
    private readonly TreeModel? _tree;
    private readonly bool _emptyFirst;
    private readonly bool _alwaysEmpty;

    public FakeUiaWalker(TreeModel? tree = null, bool emptyFirst = false, bool alwaysEmpty = false)
    {
        _tree = tree;
        _emptyFirst = emptyFirst;
        _alwaysEmpty = alwaysEmpty;
    }

    public int Calls { get; private set; }

    public TreeModel? Walk(
        nint hwnd,
        SnapshotOptions options,
        CancellationToken cancellationToken)
    {
        Calls++;

        if (_alwaysEmpty)
        {
            return null;
        }

        if (_emptyFirst && Calls == 1)
        {
            return null;
        }

        return _tree ?? SampleTree;
    }

    public static TreeModel SampleTree { get; } = new()
    {
        Role = "window",
        Name = "Notepad",
        Children =
        [
            new TreeModel
            {
                Role = "edit",
                Name = "Text editor",
                Identifier = "editor",
                Bounds = new Rect(10, 20, 800, 500),
                Focused = true,
                Actions = ["setValue"],
            },
            new TreeModel { Role = "button", Name = "Send", Actions = ["press"] },
        ],
    };
}
