using KennectComputer.Host.Actions;
using KennectComputer.Host.Uia;

namespace KennectComputer.Host.Tests;

/// <summary>
/// The expect evaluator reads a fresh accessibility tree through the walker
/// seam and checks foreground through the foreground seam, so every case here
/// is deterministic and never touches the desktop.
/// </summary>
public sealed class ExpectEvaluatorTests
{
    private const nint Hwnd = 42;

    [Fact]
    public async Task EmptyPredicateListPassesWithoutWalking()
    {
        var walker = new FakeUiaWalker(SampleTree());

        var result = await NewEvaluator(walker).EvaluateAsync(Hwnd, [], CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FailedPredicate);
        Assert.Equal(0, walker.Calls);
    }

    [Fact]
    public async Task ForegroundPredicatePassesWhenTheWindowIsForeground()
    {
        var walker = new FakeUiaWalker(SampleTree());

        var result = await NewEvaluator(walker)
            .EvaluateAsync(Hwnd, [new ExpectForeground()], CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Null(result.FailedPredicate);
        Assert.Equal(0, walker.Calls);
    }

    [Fact]
    public async Task ForegroundPredicateFailsWhenAnotherWindowIsForeground()
    {
        var walker = new FakeUiaWalker(SampleTree());

        var result = await NewEvaluator(walker, foreground: 7)
            .EvaluateAsync(Hwnd, [new ExpectForeground()], CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("foreground", result.FailedPredicate);
        Assert.Equal(0, walker.Calls);
    }

    [Fact]
    public async Task NodePredicateMatchesByRole()
        => Assert.True((await EvaluateAsync([new ExpectNode { Role = "button" }])).Ok);

    [Fact]
    public async Task NodePredicateMatchesByName()
        => Assert.True((await EvaluateAsync([new ExpectNode { Name = "Send" }])).Ok);

    [Fact]
    public async Task NodePredicateMatchesByIdentifier()
        => Assert.True((await EvaluateAsync([new ExpectNode { Identifier = "editor" }])).Ok);

    [Fact]
    public async Task NodePredicateMatchesTheCombinedSelector()
    {
        var result = await EvaluateAsync(
            [new ExpectNode { Role = "button", Name = "Send", Identifier = "send" }]);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task NodePredicateFailsWhenNoNodeMatches()
    {
        var result = await EvaluateAsync([new ExpectNode { Name = "Missing" }]);

        Assert.False(result.Ok);
        Assert.Equal("node:Missing", result.FailedPredicate);
    }

    [Fact]
    public async Task NodePredicateFailsWhenTheTreeIsUnavailable()
    {
        var evaluator = new ExpectEvaluator(
            new FakeUiaWalker(alwaysEmpty: true),
            new FakeWin32 { InitialForeground = Hwnd });

        var result = await evaluator.EvaluateAsync(
            Hwnd, [new ExpectNode { Name = "Send" }], CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("node:Send", result.FailedPredicate);
    }

    [Fact]
    public async Task NodePredicatePassesWhenTheMatchingNodeIsFocused()
    {
        var result = await EvaluateAsync([new ExpectNode { Name = "Editor", Focused = true }]);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task NodePredicateFailsWhenTheMatchingNodeIsNotFocused()
    {
        var result = await EvaluateAsync([new ExpectNode { Name = "Send", Focused = true }]);

        Assert.False(result.Ok);
        Assert.Equal("node:Send", result.FailedPredicate);
    }

    [Fact]
    public async Task NodePredicatePassesWhenTheMatchingNodeIsNotFocusedAndFocusedIsFalse()
    {
        var result = await EvaluateAsync([new ExpectNode { Name = "Send", Focused = false }]);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task NodePredicateIgnoresFocusWhenFocusedIsNotSupplied()
    {
        var result = await EvaluateAsync([new ExpectNode { Name = "Send" }]);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task TextPredicateMatchesACjkSubstringInTheCapturedValue()
    {
        var result = await EvaluateAsync(
            [new ExpectText { Name = "Editor", Contains = "こんにちは" }]);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task TextPredicateMatchesCaseInsensitively()
    {
        var result = await EvaluateAsync(
            [new ExpectText { Name = "Send", Contains = "SEND MESSAGE" }]);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task TextPredicateOnlyConsidersNodesMatchingTheSelector()
    {
        // The text exists, but not on the selected node.
        var result = await EvaluateAsync(
            [new ExpectText { Name = "Send", Contains = "こんにちは" }]);

        Assert.False(result.Ok);
        Assert.Equal("text:こんにちは", result.FailedPredicate);
    }

    [Fact]
    public async Task TextPredicateFailsWhenTheValueDoesNotContainTheText()
    {
        var result = await EvaluateAsync(
            [new ExpectText { Name = "Editor", Contains = "absent" }]);

        Assert.False(result.Ok);
        Assert.Equal("text:absent", result.FailedPredicate);
    }

    [Fact]
    public async Task ReturnsTheFirstFailingPredicateInOrder()
    {
        var result = await EvaluateAsync(
        [
            new ExpectForeground(),
            new ExpectNode { Name = "Send" },
            new ExpectText { Name = "Send", Contains = "absent" },
            new ExpectNode { Name = "Missing" },
        ]);

        Assert.False(result.Ok);
        Assert.Equal("text:absent", result.FailedPredicate);
    }

    [Fact]
    public async Task ReturnsTheForegroundFailureWhenItIsFirst()
    {
        var result = await EvaluateAsync(
            [
                new ExpectForeground(),
                new ExpectNode { Name = "Missing" },
            ],
            foreground: 7);

        Assert.False(result.Ok);
        Assert.Equal("foreground", result.FailedPredicate);
    }

    [Fact]
    public async Task WalksOncePerEvaluationForAllNodeAndTextPredicates()
    {
        var walker = new FakeUiaWalker(SampleTree());
        var evaluator = new ExpectEvaluator(walker, new FakeWin32 { InitialForeground = Hwnd });

        var result = await evaluator.EvaluateAsync(
            Hwnd,
            [
                new ExpectNode { Name = "Send" },
                new ExpectText { Name = "Editor", Contains = "こんにちは" },
                new ExpectNode { Name = "Editor", Focused = true },
            ],
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, walker.Calls);
        Assert.NotNull(walker.LastOptions);
        Assert.Equal(SnapshotOptions.DefaultMaxDepth, walker.LastOptions!.MaxDepth);
        Assert.False(walker.LastOptions.Image);
    }

    [Fact]
    public async Task EachEvaluationTakesAFreshWalk()
    {
        var walker = new FakeUiaWalker(SampleTree());
        var evaluator = new ExpectEvaluator(walker, new FakeWin32 { InitialForeground = Hwnd });

        await evaluator.EvaluateAsync(
            Hwnd, [new ExpectNode { Name = "Send" }], CancellationToken.None);
        await evaluator.EvaluateAsync(
            Hwnd, [new ExpectNode { Name = "Send" }], CancellationToken.None);

        Assert.Equal(2, walker.Calls);
    }

    [Fact]
    public async Task HonorsCancellationBeforeWalking()
    {
        var walker = new FakeUiaWalker(SampleTree());
        var evaluator = new ExpectEvaluator(walker, new FakeWin32 { InitialForeground = Hwnd });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluator.EvaluateAsync(
            Hwnd, [new ExpectNode { Name = "Send" }], cancellation.Token));

        Assert.Equal(0, walker.Calls);
    }

    private static ExpectEvaluator NewEvaluator(FakeUiaWalker walker, nint foreground = Hwnd)
        => new(walker, new FakeWin32 { InitialForeground = foreground });

    private static Task<ExpectResult> EvaluateAsync(
        ExpectPredicate[] predicates,
        nint foreground = Hwnd)
        => NewEvaluator(new FakeUiaWalker(SampleTree()), foreground)
            .EvaluateAsync(Hwnd, predicates, CancellationToken.None);

    /// <summary>
    /// A window with a focused editor holding CJK text and a plain button,
    /// enough to exercise every selector field and both focused outcomes.
    /// </summary>
    private static TreeModel SampleTree() => new()
    {
        Role = "window",
        Name = "Notepad",
        Children =
        [
            new TreeModel
            {
                Role = "edit",
                Name = "Editor",
                Identifier = "editor",
                Value = "保存しました。こんにちは世界",
                Focused = true,
            },
            new TreeModel
            {
                Role = "button",
                Name = "Send",
                Identifier = "send",
                Value = "Send message",
            },
        ],
    };
}
