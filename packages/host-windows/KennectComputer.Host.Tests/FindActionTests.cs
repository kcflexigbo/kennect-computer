using KennectComputer.Host.Actions;
using KennectComputer.Host.Control;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// Pins the find flow (DESIGN §6.7): a fresh walk of the selected window is
/// filtered by a case-insensitive text substring over the name or the captured
/// value and/or an exact role, gets a new generation, and is saved as snapshot
/// state with the current window bounds and no image, so later node actions can
/// use the matches but image-space coordinates need a fresh snapshot (R5).
/// Result caps and truncation are reported, no matches is a successful empty
/// result, and the shared selected-window resolver rejects a missing or dead
/// selection. Fakes stand in for the walker and window API, so no desktop is
/// touched.
/// </summary>
public sealed class FindActionTests
{
    private static readonly Rect InitialBounds = new(100, 50, 3000, 2000);

    [Fact]
    public async Task TextMatchesNamesAsCaseInsensitiveSubstrings()
    {
        var harness = Harness.Create(Tree(
            Button("Send"),
            Button("Search"),
            Button("Send later")));

        var result = await harness.Action.ExecuteAsync(
            new FindRequest { Text = "send" },
            CancellationToken.None);

        Assert.Equal(["Send", "Send later"], result.Matches.Select(match => match.Name));
        Assert.Equal(harness.SelectedWindow.WindowId, result.Window.WindowId);
        Assert.Equal(4, result.Scanned);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task TextAlsoMatchesTheCapturedValue()
    {
        // The protocol node has no value field; the match reads the internal
        // TreeModel value kept alongside the outline.
        var editor = new TreeModel
        {
            Role = "edit",
            Name = "Editor",
            Value = "Draft message",
            Actions = ["setValue"],
        };
        var harness = Harness.Create(Tree(Button("Send"), editor));

        var result = await harness.Action.ExecuteAsync(
            new FindRequest { Text = "draft" },
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Editor", match.Name);
        Assert.Equal("edit", match.Role);
    }

    [Fact]
    public async Task PasswordValuesAreNeverMatchable()
    {
        var password = new TreeModel
        {
            Role = "edit",
            Name = "Password",
            IsPassword = true,
            Value = "hunter2",
        };
        var harness = Harness.Create(Tree(password));

        var byValue = await harness.Action.ExecuteAsync(
            new FindRequest { Text = "hunter2" },
            CancellationToken.None);

        Assert.Empty(byValue.Matches);

        // The node still matches by its accessible name.
        var byName = await harness.Action.ExecuteAsync(
            new FindRequest { Text = "Password" },
            CancellationToken.None);

        Assert.Equal("Password", Assert.Single(byName.Matches).Name);
    }

    [Fact]
    public async Task RoleFiltersExactlyByRole()
    {
        var harness = Harness.Create(Tree(
            Button("Send"),
            new TreeModel { Role = "text", Name = "Send status" },
            new TreeModel { Role = "edit", Name = "Send note", Actions = ["setValue"] }));

        var result = await harness.Action.ExecuteAsync(
            new FindRequest { Role = "button" },
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Send", match.Name);
        Assert.Equal("button", match.Role);
    }

    [Fact]
    public async Task TextAndRoleFiltersAreCombined()
    {
        var harness = Harness.Create(Tree(
            Button("Send"),
            Button("Search"),
            new TreeModel { Role = "text", Name = "Send status" }));

        var result = await harness.Action.ExecuteAsync(
            new FindRequest { Text = "send", Role = "button" },
            CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Send", match.Name);
    }

    [Fact]
    public async Task AnEmptyFilterReturnsTheSlicedOutline()
    {
        var harness = Harness.Create(Tree(Button("Send"), Button("Search")));

        var result = await harness.Action.ExecuteAsync(
            new FindRequest(),
            CancellationToken.None);

        Assert.Equal(["Send", "Search"], result.Matches.Select(match => match.Name));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task TheWalkUsesTheFindDepthAndNodeBudgets()
    {
        var harness = Harness.Create(Tree(Button("Send")));

        await harness.Action.ExecuteAsync(new FindRequest(), CancellationToken.None);

        var options = Assert.IsType<SnapshotOptions>(harness.Walker.LastOptions);
        Assert.Equal(20, options.MaxDepth);
        Assert.Equal(5000, options.MaxNodes);
        Assert.Equal(SnapshotMode.A11y, options.Mode);
        Assert.False(options.Image);
    }

    [Fact]
    public async Task FindUsesANewGenerationAndSavesStateWithoutAnImage()
    {
        var harness = Harness.Create(Tree(Button("Send")));
        var prior = harness.SavePriorSnapshotWithImage();
        Assert.NotNull(prior.Image);

        // A find captures the window as it is now, not as the prior snapshot saw it.
        harness.MoveWindow(new Rect(200, 100, 800, 600));

        var result = await harness.Action.ExecuteAsync(
            new FindRequest { Text = "send" },
            CancellationToken.None);

        Assert.Equal(prior.Generation + 1, result.Generation);
        Assert.All(
            result.Matches,
            match => Assert.StartsWith($"n{result.Generation}:", match.NodeId));

        Assert.True(harness.Snapshots.TryGetState(harness.SelectedWindow.WindowId, out var state));
        Assert.NotNull(state);
        Assert.Equal(result.Generation, state.Generation);
        Assert.Null(state.Image);
        Assert.Equal(200, state.WindowBounds.X);
        Assert.Equal(100, state.WindowBounds.Y);
        Assert.Equal(800, state.WindowBounds.Width);
        Assert.Equal(600, state.WindowBounds.Height);

        // The saved state must be actionable: every returned ID resolves.
        Assert.All(
            result.Matches,
            match => Assert.True(
                harness.Snapshots.TryGetNode(harness.SelectedWindow.WindowId, match.NodeId, out _)));
    }

    [Fact]
    public async Task NoMatchesIsAnEmptySuccessfulResult()
    {
        var harness = Harness.Create(Tree(Button("Send"), Button("Search")));

        var result = await harness.Action.ExecuteAsync(
            new FindRequest { Text = "missing" },
            CancellationToken.None);

        Assert.Empty(result.Matches);
        Assert.Equal(3, result.Scanned);
        Assert.False(result.Truncated);
        Assert.Equal(harness.SelectedWindow.WindowId, result.Window.WindowId);
    }

    [Fact]
    public async Task MaxResultsCapsMatchesAndReportsTruncation()
    {
        var harness = Harness.Create(Tree(
            Button("Send 1"),
            Button("Send 2"),
            Button("Send 3")));

        var result = await harness.Action.ExecuteAsync(
            new FindRequest { Text = "send", MaxResults = 2 },
            CancellationToken.None);

        Assert.Equal(["Send 1", "Send 2"], result.Matches.Select(match => match.Name));
        Assert.True(result.Truncated);
        Assert.Equal(4, result.Scanned);
    }

    [Fact]
    public async Task TheDefaultResultLimitIsFifty()
    {
        var harness = Harness.Create(Tree(
            [.. Enumerable.Range(1, 51).Select(index => Button($"Send {index}"))]));

        var result = await harness.Action.ExecuteAsync(
            new FindRequest { Text = "send" },
            CancellationToken.None);

        Assert.Equal(50, result.Matches.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task TheResultLimitIsCappedAtFiveHundred()
    {
        var harness = Harness.Create(Tree(
            [.. Enumerable.Range(1, 501).Select(index => Button($"Send {index}"))]));

        var result = await harness.Action.ExecuteAsync(
            new FindRequest { Text = "send", MaxResults = 1000 },
            CancellationToken.None);

        Assert.Equal(500, result.Matches.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task ATruncatedWalkMarksTheResultTruncated()
    {
        var harness = Harness.Create(
            Tree(Button("Send")) with { Truncated = true });

        var result = await harness.Action.ExecuteAsync(
            new FindRequest { Text = "send" },
            CancellationToken.None);

        Assert.Single(result.Matches);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task NoSelectedWindowIsInvalid()
    {
        var harness = Harness.Create(Tree(Button("Send")), select: false);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new FindRequest { Text = "send" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(0, harness.Walker.Calls);
    }

    [Fact]
    public async Task ADeadWindowClearsTheSelectionAndReturnsWindowGone()
    {
        var harness = Harness.Create(Tree(Button("Send")));
        harness.Windows.MarkDead(harness.Raw.Hwnd);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new FindRequest { Text = "send" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.WindowGone, failure.Code);
        Assert.Null(harness.Selection.Selected);
        Assert.Equal(0, harness.Walker.Calls);
    }

    [Fact]
    public async Task HonorsCancellationBeforeWalking()
    {
        var harness = Harness.Create(Tree(Button("Send")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Action.ExecuteAsync(
                new FindRequest { Text = "send" },
                cancellation.Token));

        Assert.Equal(0, harness.Walker.Calls);
    }

    private static TreeModel Tree(params TreeModel[] children) => new()
    {
        Role = "window",
        Name = "Inbox",
        Children = children,
    };

    private static TreeModel Button(string name) => new()
    {
        Role = "button",
        Name = name,
        Bounds = new Rect(10, 20, 120, 32),
        Actions = ["press"],
    };

    /// <summary>
    /// One selected window plus the walker, snapshotter, store, and registry
    /// the find action needs. The snapshotter's focus/wait seams are no-ops so
    /// the tests never touch the desktop or sleep.
    /// </summary>
    private sealed class Harness
    {
        private Harness(TreeModel tree, bool select)
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
            Walker = new FakeUiaWalker(tree);
            Snapshotter = new UiaSnapshotter(Walker, _ => { }, _ => { });
            Action = new FindAction(Walker, Snapshotter, Snapshots, Selection, Registry);
        }

        public RawWindow Raw { get; }

        public FakeWindowApi Windows { get; }

        public WindowRegistry Registry { get; }

        public WindowInfo SelectedWindow { get; }

        public SelectionStore Selection { get; }

        public SnapshotStore Snapshots { get; }

        public FakeUiaWalker Walker { get; }

        public UiaSnapshotter Snapshotter { get; }

        public FindAction Action { get; }

        public static Harness Create(TreeModel tree, bool select = true)
            => new(tree, select);

        /// <summary>
        /// Saves a generation with an image, as a prior snapshot would have, so
        /// a find can prove it replaced both the generation and the image.
        /// </summary>
        public SnapshotState SavePriorSnapshotWithImage()
        {
            var generation = Snapshotter.NextGeneration();
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
                        SourceBounds = InitialBounds,
                    },
                },
                new Dictionary<string, TreeModel>(StringComparer.Ordinal),
                SelectedWindow.Bounds);

            Assert.True(Snapshots.TryGetState(SelectedWindow.WindowId, out var state));
            return state!;
        }

        /// <summary>Simulates the OS reporting the same window at new bounds.</summary>
        public void MoveWindow(Rect bounds)
            => Windows.Replace(Raw with { Bounds = bounds });
    }
}
