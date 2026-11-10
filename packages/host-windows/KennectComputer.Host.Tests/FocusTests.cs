using KennectComputer.Host.Control;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

public sealed class FocusTests
{
    [Fact]
    public async Task ReturnsFocusRefusedWhenForegroundCannotBeSet()
    {
        var service = new ForegroundService(new FakeWin32 { ForegroundSucceeds = false });

        var result = await service.FocusAsync((nint)1, TimeSpan.FromMilliseconds(50));

        Assert.False(result.Ok);
        Assert.Equal(ErrorCode.FocusRefused, result.Error);
    }

    [Fact]
    public async Task FocusesTheTargetAndVerifiesTheForeground()
    {
        var api = new FakeWin32 { InitialForeground = (nint)99 };

        var result = await new ForegroundService(api)
            .FocusAsync((nint)42, TimeSpan.FromMilliseconds(500));

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal(1, api.SetForegroundCalls);
        Assert.Equal(1, api.AttachCalls);
        Assert.Equal(1, api.DetachCalls);
        Assert.Equal((nint)42, api.CurrentForeground);
    }

    [Fact]
    public async Task RetriesOnceBeforeRefusing()
    {
        // The first foreground attempt is refused by the OS (foreground lock);
        // the single retry allowed by DESIGN §6 must succeed.
        var api = new FakeWin32 { SucceedOnCall = 2 };

        var result = await new ForegroundService(api)
            .FocusAsync((nint)42, TimeSpan.FromMilliseconds(50));

        Assert.True(result.Ok);
        Assert.Equal(2, api.SetForegroundCalls);
    }

    [Fact]
    public async Task RestoresAMinimizedTargetBeforeForegrounding()
    {
        var api = new FakeWin32 { IsMinimized = true };

        var result = await new ForegroundService(api)
            .FocusAsync((nint)42, TimeSpan.FromMilliseconds(500));

        Assert.True(result.Ok);
        Assert.Equal(1, api.RestoreCalls);
    }

    [Fact]
    public async Task ReturnsWindowGoneForAZeroHandle()
    {
        var result = await new ForegroundService(new FakeWin32())
            .FocusAsync(0, TimeSpan.FromMilliseconds(50));

        Assert.False(result.Ok);
        Assert.Equal(ErrorCode.WindowGone, result.Error);
    }

    [Fact]
    public void TheWin32ForegroundApiBindsToTheNativeExports()
    {
        // The fake exercises the service logic; this smoke test proves the real
        // P/Invoke entry points resolve at runtime.
        var api = new WindowsForegroundApi();

        Assert.NotEqual(0u, api.GetCurrentThreadId());
        _ = api.GetForegroundWindow();
        Assert.False(api.IsIconic(0));
    }
}

/// <summary>Configurable Win32 foreground seam for focus tests.</summary>
internal sealed class FakeWin32 : IForegroundApi
{
    private nint _foreground;
    private nint _initialForeground;
    private int _setForegroundCalls;

    /// <summary>The window that is foreground before any attempt.</summary>
    public nint InitialForeground
    {
        get => _initialForeground;
        init
        {
            _initialForeground = value;
            _foreground = value;
        }
    }

    public bool ForegroundSucceeds { get; init; } = true;

    /// <summary>1-based call on which <c>SetForegroundWindow</c> starts succeeding.</summary>
    public int SucceedOnCall { get; init; } = 1;

    public bool IsMinimized { get; init; }

    public uint CurrentThreadId { get; init; } = 10;

    public uint WindowThreadId { get; init; } = 20;

    public int SetForegroundCalls => _setForegroundCalls;

    public int AttachCalls { get; private set; }

    public int DetachCalls { get; private set; }

    public int RestoreCalls { get; private set; }

    public nint CurrentForeground => _foreground;

    public bool SetForegroundWindow(nint hwnd)
    {
        _setForegroundCalls++;
        if (!ForegroundSucceeds || _setForegroundCalls < SucceedOnCall)
        {
            return false;
        }

        _foreground = hwnd;
        return true;
    }

    public nint GetForegroundWindow() => _foreground;

    public uint GetCurrentThreadId() => CurrentThreadId;

    public uint GetWindowThreadProcessId(nint hwnd, out uint processId)
    {
        processId = 7;
        return WindowThreadId;
    }

    public bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach)
    {
        if (attach)
        {
            AttachCalls++;
        }
        else
        {
            DetachCalls++;
        }

        return true;
    }

    public bool IsIconic(nint hwnd) => IsMinimized;

    public bool RestoreWindow(nint hwnd)
    {
        RestoreCalls++;
        return true;
    }
}
