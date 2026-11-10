using KennectComputer.Host.Control;
using KennectComputer.Host.Policy;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

public sealed class SelectionTests
{
    [Fact]
    public async Task SelectingADeadWindowReturnsWindowGone()
    {
        var registry = new WindowRegistry(new FakeWindowApi());
        var info = registry.Register(NotepadWindow());
        registry.MarkDeadForTests(1);
        var result = await new SelectionService(registry, new AllowAllAuthorization(), new SelectionStore())
            .SelectAsync(info.WindowId, default);

        Assert.Equal(ErrorCode.WindowGone, result.Error);
        Assert.Null(result.Window);
    }

    [Fact]
    public async Task FocusingAfterTheSelectedWindowDiedReturnsWindowGone()
    {
        var registry = new WindowRegistry(new FakeWindowApi());
        var info = registry.Register(NotepadWindow());
        var selection = new SelectionStore();
        selection.Select(info);
        registry.MarkDeadForTests(1);
        var result = await new SelectionService(registry, new AllowAllAuthorization(), selection)
            .FocusSelectedAsync(default);

        Assert.Equal(ErrorCode.WindowGone, result.Error);
        Assert.Null(selection.Selected);
    }

    [Fact]
    public async Task SelectingAWindowFocusesItAndStoresTheSelection()
    {
        var raw = NotepadWindow();
        var api = new FakeWindowApi(raw);
        var registry = new WindowRegistry(api);
        var info = registry.Register(raw);
        var selection = new SelectionStore();
        var focus = new FakeWin32();

        var result = await NewService(api, registry, selection, focus)
            .SelectAsync(info.WindowId, default);

        Assert.Null(result.Error);
        Assert.NotNull(result.Window);
        Assert.Equal(info.WindowId, result.Window!.WindowId);
        Assert.True(result.Window.Focused);
        Assert.Equal(info.WindowId, selection.Selected?.WindowId);
        Assert.Equal(1, focus.SetForegroundCalls);
    }

    [Fact]
    public async Task SelectingADeniedWindowReturnsAuthorizationDenied()
    {
        var raw = NotepadWindow();
        var api = new FakeWindowApi(raw);
        var registry = new WindowRegistry(api);
        var info = registry.Register(raw);
        var selection = new SelectionStore();
        var focus = new FakeWin32();
        var authorization = new FakeAuthorization(AuthorizationDecision.Deny);

        var result = await NewService(api, registry, selection, focus, authorization)
            .SelectAsync(info.WindowId, default);

        Assert.Equal(ErrorCode.AuthorizationDenied, result.Error);
        Assert.Null(result.Window);
        Assert.Null(selection.Selected);
        Assert.Equal(0, focus.SetForegroundCalls);
        Assert.Single(authorization.RequestedPaths);
    }

    [Fact]
    public async Task AWindowThatDiesDuringAuthorizationReturnsWindowGoneWithoutFocusing()
    {
        var raw = NotepadWindow();
        var api = new FakeWindowApi(raw);
        var registry = new WindowRegistry(api);
        var info = registry.Register(raw);
        var selection = new SelectionStore();
        var focus = new FakeWin32();
        var authorization = new CallbackAuthorization(() => registry.MarkDeadForTests(raw.Hwnd));

        var result = await NewService(api, registry, selection, focus, authorization)
            .SelectAsync(info.WindowId, default);

        Assert.Equal(ErrorCode.WindowGone, result.Error);
        Assert.Null(result.Window);
        Assert.Null(selection.Selected);
        Assert.Equal(0, focus.SetForegroundCalls);
    }

    [Fact]
    public async Task SelectingByAmbiguousTitleReturnsCandidatesWithoutFocusing()
    {
        var first = NotepadWindow(hwnd: 1, pid: 100, title: "a.txt - Notepad");
        var second = NotepadWindow(hwnd: 2, pid: 101, title: "b.txt - Notepad");
        var api = new FakeWindowApi(first, second);
        var registry = new WindowRegistry(api);
        var selection = new SelectionStore();
        var focus = new FakeWin32();

        var result = await NewService(api, registry, selection, focus)
            .SelectAsync(new SelectCriteria { Title = "Notepad" }, default);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error);
        Assert.Null(result.Window);
        Assert.NotNull(result.Candidates);
        Assert.Equal(2, result.Candidates!.Count);
        Assert.Contains(result.Candidates, candidate => candidate is { Pid: 100, Title: "a.txt - Notepad" });
        Assert.Contains(result.Candidates, candidate => candidate is { Pid: 101, Title: "b.txt - Notepad" });
        Assert.All(result.Candidates, candidate => Assert.StartsWith("w_", candidate.WindowId));
        Assert.Null(selection.Selected);
        Assert.Equal(0, focus.SetForegroundCalls);
    }

    [Fact]
    public async Task SelectingBySoleProcessFocusesTheOnlyCandidate()
    {
        var notepad = NotepadWindow();
        var chrome = new RawWindow(2, 200, "Google", "chrome", new Rect(0, 0, 10, 10))
        {
            ClassName = "Chrome_WidgetWin_1",
        };
        var api = new FakeWindowApi(notepad, chrome);
        var registry = new WindowRegistry(api);
        var selection = new SelectionStore();
        var focus = new FakeWin32();

        var result = await NewService(api, registry, selection, focus)
            .SelectAsync(new SelectCriteria { Process = "notepad.exe" }, default);

        Assert.Null(result.Error);
        Assert.NotNull(result.Window);
        Assert.Equal(notepad.Pid, result.Window!.Pid);
        Assert.Equal(notepad.Title, result.Window.Title);
        Assert.Equal(result.Window.WindowId, selection.Selected?.WindowId);
        Assert.Equal(1, focus.SetForegroundCalls);
    }

    [Fact]
    public async Task AnExplicitWindowIdWinsOverAmbiguousCriteria()
    {
        var first = NotepadWindow(hwnd: 1, pid: 100, title: "a.txt - Notepad");
        var second = NotepadWindow(hwnd: 2, pid: 101, title: "b.txt - Notepad");
        var api = new FakeWindowApi(first, second);
        var registry = new WindowRegistry(api);
        var firstInfo = registry.Register(first);
        var secondInfo = registry.Register(second);
        var selection = new SelectionStore();

        var result = await NewService(api, registry, selection)
            .SelectAsync(new SelectCriteria { WindowId = secondInfo.WindowId, Title = "Notepad" }, default);

        Assert.Null(result.Error);
        Assert.NotNull(result.Window);
        Assert.Equal(secondInfo.WindowId, result.Window!.WindowId);
        Assert.Equal(secondInfo.WindowId, selection.Selected?.WindowId);
        Assert.NotEqual(firstInfo.WindowId, result.Window.WindowId);
    }

    [Fact]
    public async Task SelectingWithNoCriteriaIsRejected()
    {
        var api = new FakeWindowApi(NotepadWindow());
        var registry = new WindowRegistry(api);
        var selection = new SelectionStore();

        var result = await NewService(api, registry, selection)
            .SelectAsync(new SelectCriteria(), default);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error);
        Assert.Null(selection.Selected);
    }

    [Fact]
    public async Task FocusingWithoutASelectionIsRejected()
    {
        var api = new FakeWindowApi(NotepadWindow());
        var registry = new WindowRegistry(api);

        var result = await NewService(api, registry, new SelectionStore())
            .FocusSelectedAsync(default);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error);
    }

    private static SelectionService NewService(
        IWindowApi api,
        WindowRegistry registry,
        SelectionStore selection,
        FakeWin32? focus = null,
        IAppAuthorization? authorization = null)
        => new(
            registry,
            authorization ?? new AllowAllAuthorization(),
            selection,
            new ForegroundService(focus ?? new FakeWin32()),
            new WindowEnumerator(api, registry));

    private static RawWindow NotepadWindow(
        nint hwnd = 1,
        int pid = 100,
        string title = "Notepad",
        string processName = "Notepad")
        => new(hwnd, pid, title, processName, new Rect(0, 0, 800, 600))
        {
            ClassName = processName + "Class",
        };
}

internal sealed class CallbackAuthorization : IAppAuthorization
{
    private readonly Action _onAuthorize;

    public CallbackAuthorization(Action onAuthorize) => _onAuthorize = onAuthorize;

    public Task<AuthorizationDecision> AuthorizeAsync(
        string processPath,
        CancellationToken cancellationToken)
    {
        _onAuthorize();
        return Task.FromResult(AuthorizationDecision.AllowSession);
    }
}

internal sealed class FakeAuthorization : IAppAuthorization
{
    private readonly AuthorizationDecision _decision;

    public FakeAuthorization(AuthorizationDecision decision) => _decision = decision;

    public List<string> RequestedPaths { get; } = [];

    public Task<AuthorizationDecision> AuthorizeAsync(
        string processPath,
        CancellationToken cancellationToken)
    {
        RequestedPaths.Add(processPath);
        return Task.FromResult(_decision);
    }
}
