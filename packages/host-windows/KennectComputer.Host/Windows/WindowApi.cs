using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Windows;

/// <summary>
/// Reads top-level windows with the Win32 window APIs. Native handles stay in
/// <see cref="RawWindow"/> and never reach protocol DTOs.
/// </summary>
public sealed class WindowApi : IWindowApi
{
    private const int GwlExStyle = -20;
    private const int DwmwaCloaked = 14;
    private const int MaxTitleLength = 1024;
    private const int MaxClassNameLength = 256;

    public IReadOnlyList<RawWindow> GetTopLevelWindows()
    {
        var windows = new List<RawWindow>();
        var foreground = GetForegroundWindow();
        var processNames = new Dictionary<uint, string>();

        EnumWindowsProc callback = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            windows.Add(new RawWindow(
                hwnd,
                (int)pid,
                GetWindowTitle(hwnd),
                GetProcessName(pid, processNames),
                GetBounds(hwnd))
            {
                ClassName = GetWindowClass(hwnd),
                Visible = IsWindowVisible(hwnd),
                Cloaked = IsCloaked(hwnd),
                Minimized = IsIconic(hwnd),
                Focused = hwnd == foreground,
                ExStyle = GetExtendedStyle(hwnd),
            });

            return true;
        };

        EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        return windows;
    }

    private static Rect GetBounds(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            return new Rect();
        }

        return new Rect
        {
            X = rect.Left,
            Y = rect.Top,
            Width = Math.Max(0, rect.Right - rect.Left),
            Height = Math.Max(0, rect.Bottom - rect.Top),
        };
    }

    private static string GetWindowTitle(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(Math.Min(length + 1, MaxTitleLength));
        return GetWindowText(hwnd, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static string GetWindowClass(nint hwnd)
    {
        var buffer = new StringBuilder(MaxClassNameLength);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static string GetProcessName(uint pid, Dictionary<uint, string> cache)
    {
        if (pid == 0)
        {
            return string.Empty;
        }

        if (cache.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        string name;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            name = string.Empty;
        }
        catch (InvalidOperationException)
        {
            name = string.Empty;
        }
        catch (Win32Exception)
        {
            name = string.Empty;
        }

        cache[pid] = name;
        return name;
    }

    private static bool IsCloaked(nint hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0
                && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static long GetExtendedStyle(nint hwnd)
    {
        if (IntPtr.Size == 8)
        {
            return GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        }

        return GetWindowLong(hwnd, GwlExStyle);
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
