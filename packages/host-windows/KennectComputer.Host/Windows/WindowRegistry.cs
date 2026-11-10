using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Windows;

/// <summary>
/// A window ID resolved back to the native identity the host keeps private.
/// Identity is HWND plus PID, process name, and window class; the title is
/// metadata and may change without invalidating the ID.
/// </summary>
public sealed class ResolvedWindow
{
    public WindowInfo Info { get; init; } = new();

    public nint Hwnd { get; init; }

    public int Pid { get; init; }

    public string ProcessName { get; init; } = string.Empty;

    public string ClassName { get; init; } = string.Empty;
}

/// <summary>
/// Maps opaque <c>w_&lt;counter&gt;</c> IDs to live windows.
///
/// The same window keeps its ID across enumerations; a window whose native
/// identity changed (process restart, handle reuse) is marked dead and gets a
/// new ID. Dead IDs never resolve again, so a stale selection cannot drift to
/// another window.
/// </summary>
public sealed class WindowRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, Entry> _byHwnd = [];
    private long _nextId;

    public WindowInfo Register(RawWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (_gate)
        {
            if (_byHwnd.TryGetValue(window.Hwnd, out var existing))
            {
                if (existing.HasSameIdentity(window))
                {
                    existing.Update(window);
                    return existing.ToWindowInfo();
                }

                // The handle now belongs to a different process or class; the old
                // ID must not resolve to the new window.
                existing.Alive = false;
            }

            var entry = new Entry($"w_{++_nextId}", window);
            _byId[entry.WindowId] = entry;
            _byHwnd[window.Hwnd] = entry;
            return entry.ToWindowInfo();
        }
    }

    public bool TryResolve(string windowId, out ResolvedWindow? resolved)
    {
        if (string.IsNullOrEmpty(windowId))
        {
            resolved = null;
            return false;
        }

        lock (_gate)
        {
            if (_byId.TryGetValue(windowId, out var entry) && entry.Alive)
            {
                resolved = entry.ToResolvedWindow();
                return true;
            }
        }

        resolved = null;
        return false;
    }

    /// <summary>Simulates the OS reporting that a window died.</summary>
    internal void MarkDeadForTests(nint hwnd)
    {
        lock (_gate)
        {
            if (_byHwnd.Remove(hwnd, out var entry))
            {
                entry.Alive = false;
            }
        }
    }

    private sealed class Entry
    {
        public Entry(string windowId, RawWindow window)
        {
            WindowId = windowId;
            Hwnd = window.Hwnd;
            Pid = window.Pid;
            ProcessName = window.ProcessName;
            ClassName = window.ClassName;
            Update(window);
        }

        public string WindowId { get; }

        public nint Hwnd { get; }

        public int Pid { get; }

        public string ProcessName { get; }

        public string ClassName { get; }

        public string Title { get; private set; } = string.Empty;

        public Rect Bounds { get; private set; } = new();

        public bool Focused { get; private set; }

        public bool Minimized { get; private set; }

        public bool Visible { get; private set; }

        public bool Alive { get; set; } = true;

        public bool HasSameIdentity(RawWindow window)
            => Pid == window.Pid
                && string.Equals(ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ClassName, window.ClassName, StringComparison.Ordinal);

        public void Update(RawWindow window)
        {
            Title = window.Title;
            Focused = window.Focused;
            Minimized = window.Minimized;
            Visible = window.Visible;
            Bounds = new Rect
            {
                X = window.Bounds.X,
                Y = window.Bounds.Y,
                Width = window.Bounds.Width,
                Height = window.Bounds.Height,
            };
        }

        public WindowInfo ToWindowInfo() => new()
        {
            WindowId = WindowId,
            Title = Title,
            ProcessName = ProcessName,
            Pid = Pid,
            Bounds = new Rect
            {
                X = Bounds.X,
                Y = Bounds.Y,
                Width = Bounds.Width,
                Height = Bounds.Height,
            },
            Focused = Focused,
            Minimized = Minimized,
            Visible = Visible,
        };

        public ResolvedWindow ToResolvedWindow() => new()
        {
            Info = ToWindowInfo(),
            Hwnd = Hwnd,
            Pid = Pid,
            ProcessName = ProcessName,
            ClassName = ClassName,
        };
    }
}
