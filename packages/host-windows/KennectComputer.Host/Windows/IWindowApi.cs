using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Windows;

/// <summary>
/// A top-level window as read from the OS, before filtering and opaque ID
/// assignment. <see cref="Hwnd"/> never leaves the host.
/// </summary>
public sealed record RawWindow(nint Hwnd, int Pid, string Title, string ProcessName, Rect Bounds)
{
    /// <summary>Native window class, part of the window identity.</summary>
    public string ClassName { get; init; } = string.Empty;

    public bool Visible { get; init; } = true;

    /// <summary>True when DWM cloaks the window (for example, a virtual-desktop or suspended UWP window).</summary>
    public bool Cloaked { get; init; }

    public bool Minimized { get; init; }

    public bool Focused { get; init; }

    /// <summary>Raw <c>GWL_EXSTYLE</c> bits.</summary>
    public long ExStyle { get; init; }
}

/// <summary>Supplies the native window list to <see cref="WindowEnumerator"/>.</summary>
public interface IWindowApi
{
    IReadOnlyList<RawWindow> GetTopLevelWindows();
}
