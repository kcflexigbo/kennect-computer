using KennectComputer.Host.Actions;
using KennectComputer.Host.Control;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// Pins the read_text flow (DESIGN §6.9): the selected window and the stored
/// snapshot generation are re-checked, the live element must still match the
/// fingerprint, and its text is read through TextPattern, falling back to
/// ValuePattern. The result is capped (default 10,000, at most 50,000) and
/// reports truncation; a node with neither pattern is INVALID_REQUEST. Fakes
/// stand in for the resolver and window API, so no desktop is touched.
/// </summary>
public sealed class ReadTextActionTests
{
    private static readonly Rect InitialBounds = new(100, 50, 3000, 2000);

    [Fact]
    public async Task ReturnsTheNodesTextAndExactLength()
    {
        var target = Editor();
        var live = FakeUiaNode.From(target);
        live.Text = "hello world";
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ReadTextRequest { NodeId = "n1:1" },
            CancellationToken.None);

        Assert.Equal("hello world", result.Text);
        Assert.Equal(11, result.Length);
        Assert.Equal(result.Text.Length, result.Length);
        Assert.False(result.Truncated);
        Assert.Equal("n1:1", result.NodeId);
        Assert.Equal(harness.SelectedWindow.WindowId, result.Window.WindowId);
        Assert.Equal(["TryGetText"], live.Calls);
    }

    [Fact]
    public async Task FallsBackToTheValuePatternWhenTextIsUnavailable()
    {
        var target = Editor();
        var live = FakeUiaNode.From(target) with
        {
            SupportsGetText = false,
            Value = "draft message",
        };
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ReadTextRequest { NodeId = "n1:1" },
            CancellationToken.None);

        Assert.Equal("draft message", result.Text);
        Assert.Equal(13, result.Length);
        Assert.Equal(["TryGetText", "TryGetValue"], live.Calls);
    }

    [Fact]
    public async Task FallsBackToTheValueWhenTheTextPatternYieldsNothing()
    {
        var target = Editor();
        var live = FakeUiaNode.From(target);
        live.Text = null;
        live.Value = "stored value";
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ReadTextRequest { NodeId = "n1:1" },
            CancellationToken.None);

        Assert.Equal("stored value", result.Text);
        Assert.Equal(["TryGetText", "TryGetValue"], live.Calls);
    }

    [Fact]
    public async Task CapsAtMaxLengthAndSetsTruncated()
    {
        var target = Editor();
        var live = FakeUiaNode.From(target);
        live.Text = "abcdefghij";
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ReadTextRequest { NodeId = "n1:1", MaxLength = 4 },
            CancellationToken.None);

        Assert.Equal("abcd", result.Text);
        Assert.Equal(4, result.Length);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task TheDefaultCapIsTenThousand()
    {
        var target = Editor();
        var live = FakeUiaNode.From(target);
        live.Text = new string('a', 10_001);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ReadTextRequest { NodeId = "n1:1" },
            CancellationToken.None);

        Assert.Equal(10_000, result.Length);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task TheCapIsAtMostFiftyThousand()
    {
        var target = Editor();
        var live = FakeUiaNode.From(target);
        live.Text = new string('a', 50_001);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ReadTextRequest { NodeId = "n1:1", MaxLength = 60_000 },
            CancellationToken.None);

        Assert.Equal(50_000, result.Length);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task AnOlderNodeGenerationReturnsStaleSnapshot()
    {
        var target = Editor();
        var live = FakeUiaNode.From(target);
        var resolver = new FakeUiaNodeResolver(live);
        var harness = Harness.Create(resolver);
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ReadTextRequest { NodeId = "n2:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Null(resolver.LastTarget);
        Assert.Empty(live.Calls);
    }

    [Fact]
    public async Task ANodeWithoutAStoredSnapshotReturnsStaleSnapshot()
    {
        var live = FakeUiaNode.From(Editor());
        var resolver = new FakeUiaNodeResolver(live);
        var harness = Harness.Create(resolver);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ReadTextRequest { NodeId = "n1:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Null(resolver.LastTarget);
        Assert.Empty(live.Calls);
    }

    [Fact]
    public async Task AChangedLiveFingerprintReturnsStaleSnapshot()
    {
        var target = Editor();
        var moved = target with
        {
            Bounds = new Rect(
                target.Bounds.X + 5,
                target.Bounds.Y,
                target.Bounds.Width,
                target.Bounds.Height),
        };
        var live = FakeUiaNode.From(moved);
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ReadTextRequest { NodeId = "n1:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Assert.Empty(live.Calls);
    }

    [Fact]
    public async Task ANodeWithoutATextOrValuePatternIsInvalid()
    {
        var target = Editor();
        var live = FakeUiaNode.From(target) with
        {
            SupportsGetText = false,
            SupportsGetValue = false,
        };
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ReadTextRequest { NodeId = "n1:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(["TryGetText", "TryGetValue"], live.Calls);
    }

    [Fact]
    public async Task APasswordNodeIsInvalidAndItsContentIsNeverRead()
    {
        var target = Editor();
        var live = FakeUiaNode.From(target) with { IsPassword = true };
        live.Text = "hunter2";
        live.Value = "hunter2";
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ReadTextRequest { NodeId = "n1:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.DoesNotContain("hunter2", failure.Message);
        Assert.Empty(live.Calls);
    }

    [Fact]
    public async Task CjkTextRoundTripsUnchanged()
    {
        var target = Editor();
        const string japanese = "こんにちは、世界！";
        var live = FakeUiaNode.From(target);
        live.Text = japanese;
        var harness = Harness.Create(new FakeUiaNodeResolver(live));
        harness.SaveNode("n1:1", target);

        var result = await harness.Action.ExecuteAsync(
            new ReadTextRequest { NodeId = "n1:1" },
            CancellationToken.None);

        Assert.Equal(japanese, result.Text);
        Assert.Equal(japanese.Length, result.Length);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task NoSelectedWindowIsInvalid()
    {
        var live = FakeUiaNode.From(Editor());
        var harness = Harness.Create(new FakeUiaNodeResolver(live), select: false);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new ReadTextRequest { NodeId = "n1:1" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(live.Calls);
    }

    [Fact]
    public async Task HonorsCancellationBeforeReading()
    {
        var target = Editor();
        var live = FakeUiaNode.From(target);
        live.Text = "hello";
        var resolver = new FakeUiaNodeResolver(live);
        var harness = Harness.Create(resolver);
        harness.SaveNode("n1:1", target);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Action.ExecuteAsync(
                new ReadTextRequest { NodeId = "n1:1" },
                cancellation.Token));

        Assert.Null(resolver.LastTarget);
        Assert.Empty(live.Calls);
    }

    private static TreeModel Editor() => new()
    {
        Role = "edit",
        Name = "Message",
        Identifier = "message",
        Bounds = new Rect(10, 20, 400, 200),
        Actions = ["setValue"],
    };

    /// <summary>
    /// One selected window plus the resolver, store, and registry the read_text
    /// action needs, wired exactly as the dispatcher will wire them.
    /// </summary>
    private sealed class Harness
    {
        private Harness(IUiaNodeResolver resolver, bool select)
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
            Action = new ReadTextAction(resolver, Snapshots, Selection, Registry);
        }

        public RawWindow Raw { get; }

        public FakeWindowApi Windows { get; }

        public WindowRegistry Registry { get; }

        public WindowInfo SelectedWindow { get; }

        public SelectionStore Selection { get; }

        public SnapshotStore Snapshots { get; }

        public ReadTextAction Action { get; }

        public static Harness Create(IUiaNodeResolver resolver, bool select = true)
            => new(resolver, select);

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
    }
}
