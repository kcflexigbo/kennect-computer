using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;

namespace KennectComputer.Host.Tests;

public sealed class SnapshotStoreTests
{
    private static readonly Rect WindowBounds = new(10, 20, 800, 600);

    [Fact]
    public void SaveKeepsTheStateActionsNeed()
    {
        var store = new SnapshotStore();
        var node = new TreeModel { Role = "button", Name = "Save" };
        var nodes = new Dictionary<string, TreeModel>(StringComparer.Ordinal)
        {
            ["n1:1"] = node,
        };

        store.Save(
            "w_1",
            new Snapshot
            {
                Generation = 1,
                Nodes = [],
                Image = new ImagePayload
                {
                    Data = "x",
                    MimeType = "image/png",
                    Scale = 1.5,
                    SourceBounds = new Rect(10, 20, 800, 600),
                },
            },
            nodes,
            WindowBounds);

        Assert.True(store.TryGetState("w_1", out var state));
        Assert.NotNull(state);
        Assert.Equal(1, state.Generation);
        Assert.Equal(node, state.Nodes["n1:1"]);
        Assert.Equal(10, state.WindowBounds.X);
        Assert.Equal(20, state.WindowBounds.Y);
        Assert.Equal(800, state.WindowBounds.Width);
        Assert.Equal(600, state.WindowBounds.Height);
        Assert.NotNull(state.Image);
        Assert.Equal(1.5, state.Image.Scale);
        Assert.Equal(10, state.Image.SourceBounds.X);
        Assert.Equal(20, state.Image.SourceBounds.Y);
        Assert.Equal(800, state.Image.SourceBounds.Width);
        Assert.Equal(600, state.Image.SourceBounds.Height);
    }

    [Fact]
    public void SaveWithoutAnImageKeepsNullImageMetadata()
    {
        var store = new SnapshotStore();

        store.Save(
            "w_1",
            new Snapshot { Generation = 1, Nodes = [] },
            new Dictionary<string, TreeModel>(StringComparer.Ordinal),
            WindowBounds);

        Assert.True(store.TryGetState("w_1", out var state));
        Assert.NotNull(state);
        Assert.Null(state.Image);
    }

    [Fact]
    public void TryGetNodeResolvesOnlyTheSavedGeneration()
    {
        var store = new SnapshotStore();
        var node = new TreeModel { Role = "button", Name = "Save" };

        store.Save(
            "w_1",
            new Snapshot { Generation = 1, Nodes = [] },
            new Dictionary<string, TreeModel>(StringComparer.Ordinal)
            {
                ["n1:1"] = node,
            },
            WindowBounds);

        Assert.True(store.TryGetNode("w_1", "n1:1", out var found));
        Assert.Equal(node, found);
        Assert.False(store.TryGetNode("w_1", "n2:1", out _));
    }

    [Fact]
    public void ASecondSaveReplacesTheStateAndItsGeneration()
    {
        var store = new SnapshotStore();
        var first = new TreeModel { Role = "button", Name = "First" };
        var second = new TreeModel { Role = "button", Name = "Second" };
        var secondBounds = new Rect(5, 6, 700, 500);

        store.Save(
            "w_1",
            new Snapshot { Generation = 1, Nodes = [] },
            new Dictionary<string, TreeModel>(StringComparer.Ordinal)
            {
                ["n1:1"] = first,
            },
            WindowBounds);
        store.Save(
            "w_1",
            new Snapshot { Generation = 2, Nodes = [] },
            new Dictionary<string, TreeModel>(StringComparer.Ordinal)
            {
                ["n2:1"] = second,
            },
            secondBounds);

        Assert.False(store.TryGetNode("w_1", "n1:1", out _));
        Assert.True(store.TryGetNode("w_1", "n2:1", out var current));
        Assert.Equal(second, current);
        Assert.True(store.TryGetState("w_1", out var state));
        Assert.NotNull(state);
        Assert.Equal(2, state.Generation);
        Assert.Equal(secondBounds.X, state.WindowBounds.X);
        Assert.Equal(secondBounds.Width, state.WindowBounds.Width);
        Assert.Single(state.Nodes);
    }

    [Fact]
    public void ClearRemovesTheState()
    {
        var store = new SnapshotStore();
        store.Save(
            "w_1",
            new Snapshot { Generation = 1, Nodes = [] },
            new Dictionary<string, TreeModel>(StringComparer.Ordinal)
            {
                ["n1:1"] = new TreeModel { Role = "button", Name = "Save" },
            },
            WindowBounds);

        store.Clear("w_1");

        Assert.False(store.TryGetState("w_1", out _));
        Assert.False(store.TryGetNode("w_1", "n1:1", out _));
    }

    [Fact]
    public void NextGenerationIsStrictlyIncreasingAndCaptureSharesIt()
    {
        var snapshotter = new UiaSnapshotter(new FakeUiaWalker());

        var first = snapshotter.NextGeneration();
        var second = snapshotter.NextGeneration();

        Assert.True(second > first);

        var firstCapture = snapshotter.Capture(
            1,
            WindowBounds,
            new SnapshotOptions(),
            CancellationToken.None);
        var secondCapture = snapshotter.Capture(
            1,
            WindowBounds,
            new SnapshotOptions(),
            CancellationToken.None);

        Assert.True(firstCapture.Snapshot.Generation > second);
        Assert.True(secondCapture.Snapshot.Generation > firstCapture.Snapshot.Generation);
        Assert.All(
            firstCapture.Snapshot.Nodes,
            node => Assert.StartsWith($"n{firstCapture.Snapshot.Generation}:", node.NodeId));
        Assert.All(
            secondCapture.Snapshot.Nodes,
            node => Assert.StartsWith($"n{secondCapture.Snapshot.Generation}:", node.NodeId));
    }

    [Fact]
    public void NextGenerationIsThreadSafe()
    {
        var snapshotter = new UiaSnapshotter(new FakeUiaWalker());
        var generations = new int[500];

        Parallel.For(0, generations.Length, index =>
        {
            generations[index] = snapshotter.NextGeneration();
        });

        Assert.Equal(generations.Length, generations.Distinct().Count());
        Assert.All(generations, generation => Assert.InRange(generation, 1, generations.Length));
    }
}
