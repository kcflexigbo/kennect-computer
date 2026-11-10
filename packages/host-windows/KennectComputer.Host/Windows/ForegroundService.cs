using System.Runtime.InteropServices;

using KennectComputer.Host.Control;

namespace KennectComputer.Host.Windows;

/// <summary>
/// The Win32 window-foreground operations, behind a seam so focus refusal and
/// the attach-input workaround can be tested without touching the desktop.
/// </summary>
public interface IForegroundApi
{
    bool SetForegroundWindow(nint hwnd);

    nint GetForegroundWindow();

    uint GetCurrentThreadId();

    uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    bool IsIconic(nint hwnd);

    bool RestoreWindow(nint hwnd);
}

/// <summary>Win32 implementation of the foreground operations.</summary>
public sealed class WindowsForegroundApi : IForegroundApi
{
    private const int SwRestore = 9;

    public bool SetForegroundWindow(nint hwnd) => SetForegroundWindowNative(hwnd);

    public nint GetForegroundWindow() => GetForegroundWindowNative();

    public uint GetCurrentThreadId() => GetCurrentThreadIdNative();

    public uint GetWindowThreadProcessId(nint hwnd, out uint processId)
        => GetWindowThreadProcessIdNative(hwnd, out processId);

    public bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach)
        => AttachThreadInputNative(idAttach, idAttachTo, attach);

    public bool IsIconic(nint hwnd) => IsIconicNative(hwnd);

    public bool RestoreWindow(nint hwnd) => ShowWindow(hwnd, SwRestore);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindowNative(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint GetForegroundWindowNative();

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentThreadIdNative();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
    private static extern uint GetWindowThreadProcessIdNative(nint hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "AttachThreadInput", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInputNative(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconicNative(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);
}

/// <summary>
/// Brings a window to the foreground using the <c>AttachThreadInput</c>
/// workaround, verifies <c>GetForegroundWindow</c> rather than trusting the
/// return value, retries once (DESIGN §6), then refuses with
/// <c>FOCUS_REFUSED</c>. Input is never sent to a window that was not verified
/// foreground.
/// </summary>
public sealed class ForegroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private const int MaxAttempts = 2;

    private readonly IForegroundApi _api;

    public ForegroundService(IForegroundApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public async Task<FocusResult> FocusAsync(nint hwnd, TimeSpan timeout)
    {
        if (hwnd == 0)
        {
            return FocusResult.Failed(ErrorCode.WindowGone);
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            SetForeground(hwnd);

            if (await WaitForForegroundAsync(hwnd, timeout).ConfigureAwait(false))
            {
                return FocusResult.Success;
            }
        }

        return FocusResult.Failed(ErrorCode.FocusRefused);
    }

    private void SetForeground(nint hwnd)
    {
        var foreground = _api.GetForegroundWindow();
        var currentThread = _api.GetCurrentThreadId();
        var foregroundThread = foreground == 0
            ? 0
            : _api.GetWindowThreadProcessId(foreground, out _);

        var attached = foregroundThread != 0
            && foregroundThread != currentThread
            && _api.AttachThreadInput(currentThread, foregroundThread, attach: true);

        try
        {
            if (_api.IsIconic(hwnd))
            {
                _api.RestoreWindow(hwnd);
            }

            _api.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                _api.AttachThreadInput(currentThread, foregroundThread, attach: false);
            }
        }
    }

    private async Task<bool> WaitForForegroundAsync(nint hwnd, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            if (_api.GetForegroundWindow() == hwnd)
            {
                return true;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval)
                .ConfigureAwait(false);
        }
    }
}
