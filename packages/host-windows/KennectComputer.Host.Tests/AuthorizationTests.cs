using KennectComputer.Host.Config;
using KennectComputer.Host.Policy;

namespace KennectComputer.Host.Tests;

public sealed class AuthorizationTests
{
    [Fact]
    public async Task PreAuthorizedAppSkipsThePrompt()
    {
        var prompt = new FakeAuthorizationPrompt();
        var auth = new AppAuthorization(
            new AppConfig { Apps = { ["Feishu"] = new AppPolicy { PreAuthorized = true } } },
            prompt);

        var decision = await auth.AuthorizeAsync(@"C:\Apps\Feishu\Feishu.exe", default);

        Assert.Equal(AuthorizationDecision.AllowSession, decision);
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task PreAuthorizedMatchingIsCaseInsensitiveAndIgnoresTheExeSuffix()
    {
        var prompt = new FakeAuthorizationPrompt();
        var auth = new AppAuthorization(
            new AppConfig { Apps = { ["feishu.exe"] = new AppPolicy { PreAuthorized = true } } },
            prompt);

        var decision = await auth.AuthorizeAsync(@"C:\APPS\FEISHU.EXE", default);

        Assert.Equal(AuthorizationDecision.AllowSession, decision);
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task DeniedAppIsDeniedForTheSession()
    {
        var prompt = new FakeAuthorizationPrompt(AuthorizationDecision.Deny);
        var auth = new AppAuthorization(new AppConfig(), prompt);

        Assert.Equal(
            AuthorizationDecision.Deny,
            await auth.AuthorizeAsync(@"C:\Apps\Other\Other.exe", default));
        Assert.Equal(
            AuthorizationDecision.Deny,
            await auth.AuthorizeAsync(@"C:\Apps\Other\Other.exe", default));
        Assert.Equal(1, prompt.Calls);
    }

    [Fact]
    public async Task AllowSessionIsCachedForTheProcessPath()
    {
        var prompt = new FakeAuthorizationPrompt(AuthorizationDecision.AllowSession);
        var auth = new AppAuthorization(new AppConfig(), prompt);

        Assert.Equal(
            AuthorizationDecision.AllowSession,
            await auth.AuthorizeAsync(@"C:\Apps\Other\Other.exe", default));
        Assert.Equal(
            AuthorizationDecision.AllowSession,
            await auth.AuthorizeAsync(@"C:\Apps\Other\Other.exe", default));
        Assert.Equal(1, prompt.Calls);
    }

    [Fact]
    public async Task AllowOnceIsNotCached()
    {
        var prompt = new FakeAuthorizationPrompt(
            AuthorizationDecision.AllowOnce,
            AuthorizationDecision.AllowOnce);
        var auth = new AppAuthorization(new AppConfig(), prompt);

        Assert.Equal(
            AuthorizationDecision.AllowOnce,
            await auth.AuthorizeAsync(@"C:\Apps\Other\Other.exe", default));
        Assert.Equal(
            AuthorizationDecision.AllowOnce,
            await auth.AuthorizeAsync(@"C:\Apps\Other\Other.exe", default));
        Assert.Equal(2, prompt.Calls);
    }

    [Fact]
    public async Task DecisionsAreCachedPerFullProcessPath()
    {
        var prompt = new FakeAuthorizationPrompt(
            AuthorizationDecision.Deny,
            AuthorizationDecision.AllowSession);
        var auth = new AppAuthorization(new AppConfig(), prompt);

        Assert.Equal(
            AuthorizationDecision.Deny,
            await auth.AuthorizeAsync(@"C:\Apps\First\App.exe", default));
        Assert.Equal(
            AuthorizationDecision.Deny,
            await auth.AuthorizeAsync(@"C:\Apps\First\App.exe", default));
        Assert.Equal(
            AuthorizationDecision.AllowSession,
            await auth.AuthorizeAsync(@"C:\Apps\Second\App.exe", default));
        Assert.Equal(2, prompt.Calls);
    }

    [Fact]
    public async Task AnEmptyOrUnresolvedPathIsDeniedWithoutPrompting()
    {
        var prompt = new FakeAuthorizationPrompt();
        var auth = new AppAuthorization(new AppConfig(), prompt);

        Assert.Equal(AuthorizationDecision.Deny, await auth.AuthorizeAsync(string.Empty, default));
        Assert.Equal(AuthorizationDecision.Deny, await auth.AuthorizeAsync("   ", default));
        Assert.Equal(AuthorizationDecision.Deny, await auth.AuthorizeAsync("Other.exe", default));
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task APromptFailureFailsClosed()
    {
        var auth = new AppAuthorization(new AppConfig(), new ThrowingAuthorizationPrompt());

        Assert.Equal(AuthorizationDecision.Deny, await auth.AuthorizeAsync(@"C:\Apps\App\App.exe", default));
    }

    [Fact]
    public async Task CancellationPropagatesWithoutCachingADecision()
    {
        var prompt = new FakeAuthorizationPrompt();
        var auth = new AppAuthorization(new AppConfig(), prompt);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => auth.AuthorizeAsync(@"C:\Apps\Other\Other.exe", cancellation.Token));

        // Nothing was cached, so a later call still prompts.
        await auth.AuthorizeAsync(@"C:\Apps\Other\Other.exe", default);
        Assert.Equal(1, prompt.Calls);
    }

    [Fact]
    public async Task CancellationClosesTheAuthorizationDialog()
    {
        // A stop abandons the wait on the tray thread but must also close the
        // modal; otherwise the host's own dialog lingers on screen.
        var view = new FakeAuthorizationDialogView();
        var prompt = new WinFormsAuthorizationPrompt(work => Task.Run(work), _ => view);
        using var cancellation = new CancellationTokenSource();

        var ask = prompt.AskAsync(@"C:\Apps\App\App.exe", cancellation.Token);
        await view.AskStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask);
        Assert.Equal(1, view.CloseCalls);
        await view.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AStopBeforeTheDialogIsCreatedDoesNotShowIt()
    {
        var view = new FakeAuthorizationDialogView();
        var prompt = new WinFormsAuthorizationPrompt(work => Task.Run(work), _ => view);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => prompt.AskAsync(@"C:\Apps\App\App.exe", cancellation.Token));

        Assert.Equal(0, view.AskCalls);
        Assert.Equal(0, view.CloseCalls);
    }
}

internal sealed class FakeAuthorizationDialogView : IAuthorizationDialogView
{
    private readonly ManualResetEventSlim _closed = new(initialState: false);

    public TaskCompletionSource AskStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Disposed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int AskCalls { get; private set; }

    public int CloseCalls { get; private set; }

    public AuthorizationDecision Ask()
    {
        AskCalls++;
        AskStarted.TrySetResult();
        _closed.Wait(TimeSpan.FromSeconds(10));
        return AuthorizationDecision.Deny;
    }

    public void Close()
    {
        CloseCalls++;
        _closed.Set();
    }

    public void Dispose() => Disposed.TrySetResult();
}

internal sealed class FakeAuthorizationPrompt : IAuthorizationPrompt
{
    private readonly Queue<AuthorizationDecision> _decisions;

    public FakeAuthorizationPrompt(params AuthorizationDecision[] decisions)
        => _decisions = new Queue<AuthorizationDecision>(
            decisions.Length == 0 ? [AuthorizationDecision.AllowSession] : decisions);

    public int Calls { get; private set; }

    public Task<AuthorizationDecision> AskAsync(
        string processPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        var decision = _decisions.Count > 0
            ? _decisions.Dequeue()
            : AuthorizationDecision.AllowSession;
        return Task.FromResult(decision);
    }
}

internal sealed class ThrowingAuthorizationPrompt : IAuthorizationPrompt
{
    public Task<AuthorizationDecision> AskAsync(
        string processPath,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException("The prompt UI is unavailable.");
}
