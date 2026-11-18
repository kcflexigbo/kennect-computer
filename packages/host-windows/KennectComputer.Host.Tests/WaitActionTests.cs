using KennectComputer.Host.Actions;
using KennectComputer.Host.Control;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// Pins the wait flow (DESIGN §6.8): a window, node, or node text condition is
/// polled on the injected clock until it is observed (or, with
/// <c>present: false</c>, until it is gone), then returns; the deadline is
/// enforced with <c>ACTION_TIMEOUT</c> naming the condition, cancellation
/// propagates unchanged, and no input is ever sent. Window conditions need no
/// selection; node and text conditions walk the selected window through the
/// shared preflight resolver. Fakes stand in for the walker, windows, and
/// clock, so the tests never touch the desktop or sleep.
/// </summary>
public sealed class WaitActionTests
{
    private static readonly Rect WindowBounds = new(100, 50, 800, 600);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ANodeAppearsOnTheThirdPoll()
    {
        var harness = Harness.ForNodes(call => call < 3 ? null : Tree(Button("Send")));
        var condition = new WaitNodeCondition { Role = "button", Name = "Send" };

        var result = await harness.Action.ExecuteAsync(
            new WaitRequest { Condition = condition },
            CancellationToken.None);

        Assert.Same(condition, result.Condition);
        Assert.Null(result.Window);
        Assert.Equal(200L, result.ElapsedMs);
        Assert.Equal(3, harness.Walker.Calls);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)],
            harness.Clock.Delays);
    }

    [Fact]
    public async Task PresentFalseReturnsWhenTheNodeDisappears()
    {
        var harness = Harness.ForNodes(
            call => call < 3 ? Tree(Button("Send")) : Tree(Button("Search")));

        var result = await harness.Action.ExecuteAsync(
            new WaitRequest
            {
                Condition = new WaitNodeCondition { Name = "Send" },
                Present = false,
            },
            CancellationToken.None);

        Assert.Equal(200L, result.ElapsedMs);
        Assert.Equal(3, harness.Walker.Calls);
        Assert.Null(result.Window);
    }

    [Theory]
    [InlineData("Feishu.exe", "Feishu")]
    [InlineData("Feishu", "Feishu.exe")]
    [InlineData("FEISHU.EXE", "feishu")]
    public async Task AWindowConditionMatchesByProcess(string actual, string requested)
    {
        var harness = Harness.ForWindows(
            Notepad,
            new RawWindow(2, 200, "Feishu", actual, WindowBounds) { ClassName = "FeishuClass" });
        var condition = new WaitWindowCondition { Process = requested };

        var result = await harness.Action.ExecuteAsync(
            new WaitRequest { Condition = condition },
            CancellationToken.None);

        Assert.NotNull(result.Window);
        Assert.Equal(actual, result.Window.ProcessName);
        Assert.Equal(0L, result.ElapsedMs);
        Assert.Equal(0, harness.Walker.Calls);
        Assert.Empty(harness.Clock.Delays);
    }

    [Fact]
    public async Task AWindowConditionMatchesATitleSubstring()
    {
        var harness = Harness.ForWindows(
            Notepad,
            new RawWindow(2, 200, "Inbox - Feishu", "Feishu.exe", WindowBounds) { ClassName = "FeishuClass" });

        var result = await harness.Action.ExecuteAsync(
            new WaitRequest { Condition = new WaitWindowCondition { Title = "inbox" } },
            CancellationToken.None);

        Assert.NotNull(result.Window);
        Assert.Equal("Inbox - Feishu", result.Window.Title);
    }

    [Fact]
    public async Task AWindowConditionRequiresBothProcessAndTitleWhenBothAreGiven()
    {
        var harness = Harness.ForWindows(
            new RawWindow(1, 100, "Calendar", "Feishu.exe", WindowBounds) { ClassName = "FeishuClass" },
            new RawWindow(2, 200, "Inbox", "Notepad", WindowBounds) { ClassName = "NotepadClass" },
            new RawWindow(3, 300, "Inbox", "Feishu.exe", WindowBounds) { ClassName = "FeishuClass" });

        var result = await harness.Action.ExecuteAsync(
            new WaitRequest
            {
                Condition = new WaitWindowCondition { Process = "feishu", Title = "Inbox" },
            },
            CancellationToken.None);

        Assert.NotNull(result.Window);
        Assert.Equal(300, result.Window.Pid);
    }

    [Fact]
    public async Task AWindowDisappearsWithPresentFalse()
    {
        var feishu = new RawWindow(2, 200, "Inbox", "Feishu.exe", WindowBounds)
        {
            ClassName = "FeishuClass",
        };
        var harness = Harness.ForWindows(Notepad, feishu);
        harness.Clock.OnDelay(_ => harness.Api.MarkDead(feishu.Hwnd));

        var result = await harness.Action.ExecuteAsync(
            new WaitRequest
            {
                Condition = new WaitWindowCondition { Process = "Feishu" },
                Present = false,
            },
            CancellationToken.None);

        Assert.Null(result.Window);
        Assert.Equal(100L, result.ElapsedMs);
        Assert.Single(harness.Clock.Delays);
    }

    [Fact]
    public async Task ANodeConditionMatchesRoleNameAndIdentifier()
    {
        var target = new TreeModel
        {
            Role = "button",
            Name = "Send",
            Identifier = "sendButton",
            Actions = ["press"],
        };
        var lookalike = new TreeModel
        {
            Role = "text",
            Name = "Send",
            Identifier = "sendLabel",
        };
        var harness = Harness.ForNodes(_ => Tree(lookalike, target));

        var result = await harness.Action.ExecuteAsync(
            new WaitRequest
            {
                Condition = new WaitNodeCondition
                {
                    Role = "button",
                    Name = "Send",
                    Identifier = "sendButton",
                },
            },
            CancellationToken.None);

        Assert.Equal(0L, result.ElapsedMs);
        Assert.Equal(1, harness.Walker.Calls);
    }

    [Fact]
    public async Task TextMatchingIsCaseInsensitiveAndUsesTheValue()
    {
        var editor = new TreeModel
        {
            Role = "edit",
            Name = "Editor",
            Value = "Draft READY to send",
            Actions = ["setValue"],
        };
        var harness = Harness.ForNodes(_ => Tree(editor));

        var result = await harness.Action.ExecuteAsync(
            new WaitRequest { Condition = new WaitTextCondition { Contains = "ready" } },
            CancellationToken.None);

        Assert.Equal(0L, result.ElapsedMs);
        Assert.Equal(1, harness.Walker.Calls);
    }

    [Fact]
    public async Task TextMatchingIgnoresTheAccessibleName()
    {
        // The name says "Ready" but the captured value does not: a text
        // condition reads TreeModel.Value only.
        var node = new TreeModel { Role = "button", Name = "Ready", Value = "idle" };
        var harness = Harness.ForNodes(_ => Tree(node));

        var result = await harness.Action.ExecuteAsync(
            new WaitRequest
            {
                Condition = new WaitTextCondition { Contains = "ready" },
                Present = false,
            },
            CancellationToken.None);

        Assert.Equal(0L, result.ElapsedMs);
    }

    [Fact]
    public async Task TextConditionsOnlyMatchNodesThatMatchTheSelector()
    {
        // The only value containing the text belongs to a text node, so a
        // condition restricted to edit nodes is absent and present:false
        // succeeds on the first poll.
        var status = new TreeModel { Role = "text", Name = "Status", Value = "Ready" };
        var harness = Harness.ForNodes(_ => Tree(status));

        var result = await harness.Action.ExecuteAsync(
            new WaitRequest
            {
                Condition = new WaitTextCondition { Role = "edit", Contains = "ready" },
                Present = false,
            },
            CancellationToken.None);

        Assert.Equal(0L, result.ElapsedMs);
    }

    [Fact]
    public async Task ATimeoutReturnsActionTimeoutAndNamesTheCondition()
    {
        var harness = Harness.ForNodes(_ => Tree(Button("Search")));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new WaitRequest
                {
                    Condition = new WaitNodeCondition { Role = "button", Name = "Send" },
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.ActionTimeout, failure.Code);
        Assert.Contains("10000", failure.Message);
        Assert.Contains("button", failure.Message);
        Assert.Contains("Send", failure.Message);
        Assert.Contains("appear", failure.Message);
    }

    [Fact]
    public async Task TheDefaultTimeoutIsTenSecondsAndTheDefaultPollIsOneHundredMilliseconds()
    {
        var harness = Harness.ForNodes(_ => null);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new WaitRequest { Condition = new WaitNodeCondition { Name = "Send" } },
                CancellationToken.None));

        Assert.Equal(ErrorCode.ActionTimeout, failure.Code);
        Assert.Equal(100, harness.Walker.Calls);
        Assert.Equal(100, harness.Clock.Delays.Count);
        Assert.All(
            harness.Clock.Delays,
            delay => Assert.Equal(TimeSpan.FromMilliseconds(100), delay));
        Assert.Equal(TimeSpan.FromSeconds(10), harness.Clock.TotalDelay);
    }

    [Fact]
    public async Task ThePollIsClampedToAtLeastTenMilliseconds()
    {
        var harness = Harness.ForNodes(_ => null);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new WaitRequest
                {
                    Condition = new WaitNodeCondition { Name = "Send" },
                    TimeoutMs = 30,
                    PollMs = 1,
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.ActionTimeout, failure.Code);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10),
            ],
            harness.Clock.Delays);
    }

    [Fact]
    public async Task ThePollIsClampedToAtMostOneSecond()
    {
        var harness = Harness.ForNodes(_ => null);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new WaitRequest
                {
                    Condition = new WaitNodeCondition { Name = "Send" },
                    TimeoutMs = 2_500,
                    PollMs = 5_000,
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.ActionTimeout, failure.Code);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(1_000),
                TimeSpan.FromMilliseconds(1_000),
                TimeSpan.FromMilliseconds(500),
            ],
            harness.Clock.Delays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(60_001)]
    public async Task AnInvalidTimeoutIsRejectedBeforePolling(int timeoutMs)
    {
        var harness = Harness.ForNodes(_ => Tree(Button("Send")));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new WaitRequest
                {
                    Condition = new WaitNodeCondition { Name = "Send" },
                    TimeoutMs = timeoutMs,
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(0, harness.Walker.Calls);
    }

    [Fact]
    public async Task TheMaximumTimeoutIsAccepted()
    {
        var harness = Harness.ForNodes(_ => Tree(Button("Send")));

        var result = await harness.Action.ExecuteAsync(
            new WaitRequest
            {
                Condition = new WaitNodeCondition { Name = "Send" },
                TimeoutMs = 60_000,
            },
            CancellationToken.None);

        Assert.Equal(0L, result.ElapsedMs);
    }

    [Fact]
    public async Task AMissingConditionIsInvalid()
    {
        var harness = Harness.ForNodes(_ => Tree(Button("Send")));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(new WaitRequest(), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(0, harness.Walker.Calls);
    }

    [Fact]
    public async Task AWindowConditionWithoutProcessOrTitleIsInvalid()
    {
        var harness = Harness.ForWindows(Notepad);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new WaitRequest { Condition = new WaitWindowCondition() },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
    }

    [Fact]
    public async Task ANodeConditionWithoutASelectorIsInvalid()
    {
        var harness = Harness.ForNodes(_ => Tree(Button("Send")));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new WaitRequest { Condition = new WaitNodeCondition() },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(0, harness.Walker.Calls);
    }

    [Fact]
    public async Task ATextConditionWithoutContainsIsInvalid()
    {
        var harness = Harness.ForNodes(_ => Tree(Button("Send")));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new WaitRequest { Condition = new WaitTextCondition { Contains = " " } },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(0, harness.Walker.Calls);
    }

    [Fact]
    public async Task NoSelectedWindowIsInvalidForNodeAndTextConditions()
    {
        var harness = Harness.ForNodes(_ => Tree(Button("Send")), select: false);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new WaitRequest { Condition = new WaitNodeCondition { Name = "Send" } },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(0, harness.Walker.Calls);
    }

    [Fact]
    public async Task CancellationAbortsPromptlyAndIsNotATimeout()
    {
        var harness = Harness.ForNodes(_ => null);
        using var cancellation = new CancellationTokenSource();
        harness.Clock.OnDelay(_ => cancellation.Cancel());

        var failure = await Record.ExceptionAsync(() =>
            harness.Action.ExecuteAsync(
                new WaitRequest
                {
                    Condition = new WaitNodeCondition { Name = "Send" },
                    TimeoutMs = 60_000,
                },
                cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(1, harness.Walker.Calls);
        Assert.Single(harness.Clock.Delays);
    }

    [Fact]
    public async Task HonorsCancellationBeforeTheFirstWalk()
    {
        var harness = Harness.ForNodes(_ => Tree(Button("Send")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Action.ExecuteAsync(
                new WaitRequest { Condition = new WaitNodeCondition { Name = "Send" } },
                cancellation.Token));

        Assert.Equal(0, harness.Walker.Calls);
        Assert.Empty(harness.Clock.Delays);
    }

    private static RawWindow Notepad
        => new(1, 100, "Notepad", "Notepad", WindowBounds) { ClassName = "NotepadClass" };

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
    /// The action plus fakes for the accessibility walk, the window list, and
    /// the clock. Node and text tests select a registered Notepad window;
    /// window tests run with no selection, proving a window condition needs
    /// none.
    /// </summary>
    private sealed class Harness
    {
        private Harness(FakeWindowApi api, Func<int, TreeModel?> walk, bool select)
        {
            Api = api;
            Registry = new WindowRegistry(api);
            Windows = new WindowEnumerator(api, Registry);
            Selection = new SelectionStore();

            if (select)
            {
                Selection.Select(Registry.Register(Notepad));
            }

            Clock = new FakeWaitClock(Start);
            Walker = new ScriptedWalker(walk);
            Action = new WaitAction(Walker, Windows, Selection, Registry, Clock);
        }

        public FakeWindowApi Api { get; }

        public WindowRegistry Registry { get; }

        public WindowEnumerator Windows { get; }

        public SelectionStore Selection { get; }

        public FakeWaitClock Clock { get; }

        public ScriptedWalker Walker { get; }

        public WaitAction Action { get; }

        public static Harness ForNodes(Func<int, TreeModel?> walk, bool select = true)
            => new(new FakeWindowApi(Notepad), walk, select);

        public static Harness ForWindows(params RawWindow[] windows)
            => new(new FakeWindowApi(windows), _ => null, select: false);
    }

    /// <summary>A walker whose tree depends on the one-based call number.</summary>
    private sealed class ScriptedWalker : IUiaWalker
    {
        private readonly Func<int, TreeModel?> _onWalk;

        public ScriptedWalker(Func<int, TreeModel?> onWalk) => _onWalk = onWalk;

        public int Calls { get; private set; }

        public TreeModel? Walk(
            nint hwnd,
            SnapshotOptions options,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return _onWalk(Calls);
        }
    }

    /// <summary>
    /// A clock that never sleeps: each delay advances the fake time and may
    /// run a hook, so tests can cancel or change the desktop between polls.
    /// </summary>
    private sealed class FakeWaitClock : IWaitClock
    {
        private readonly List<TimeSpan> _delays = [];
        private Action<int>? _onDelay;

        public FakeWaitClock(DateTimeOffset start) => UtcNow = start;

        public DateTimeOffset UtcNow { get; private set; }

        public IReadOnlyList<TimeSpan> Delays => _delays;

        public TimeSpan TotalDelay => TimeSpan.FromTicks(_delays.Sum(delay => delay.Ticks));

        public FakeWaitClock OnDelay(Action<int> onDelay)
        {
            _onDelay = onDelay;
            return this;
        }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _delays.Add(delay);
            UtcNow += delay;
            _onDelay?.Invoke(_delays.Count);
            return Task.CompletedTask;
        }
    }
}
