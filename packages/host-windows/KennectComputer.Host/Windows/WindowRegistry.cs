using KennectComputer.Host.Policy;
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
/// new ID. Every resolve re-checks the live native window (handle, PID, process,
/// and class) and re-applies the privacy policy to its current process and
/// title, so an ID cannot outlive the window or become private after issue even
/// when no new enumeration has run since. Dead IDs are removed and never resolve
/// again, so a stale selection cannot drift to another window.
///
/// IDs start at a random per-run origin, so an ID handed out by an earlier host
/// process never collides with an ID from a later one. The MCP server reconnects
/// transparently after a host restart; a stale <c>w_&lt;counter&gt;</c> must fail
/// instead of resolving to whatever window now holds that counter.
/// </summary>
public sealed class WindowRegistry
{
    /// <summary>
    /// IDs reserved for one host run. The next run starts at least this far
    /// away, so two runs cannot produce the same ID.
    /// </summary>
    internal const long IdSpace = 1L << 20;

    private const long IdOriginRange = 1L << 42;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, Entry> _byHwnd = [];
    private readonly IWindowApi _api;
    private readonly IExclusionPolicy? _exclusions;
    private readonly long _idOrigin;
    private long _nextId;

    public WindowRegistry(IWindowApi api, IExclusionPolicy? exclusions = null)
        : this(api, exclusions, Random.Shared.NextInt64(IdOriginRange) * IdSpace)
    {
    }

    /// <summary>Creates a registry with a fixed ID origin; tests use this.</summary>
    internal WindowRegistry(
        IWindowApi api,
        IExclusionPolicy? exclusions,
        long idOrigin)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _exclusions = exclusions;
        _idOrigin = idOrigin;
    }

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
                Invalidate(existing);
            }

            var entry = new Entry($"w_{_idOrigin + ++_nextId}", window);
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
            if (!_byId.TryGetValue(windowId, out var entry) || !entry.Alive)
            {
                resolved = null;
                return false;
            }

            // The window may have died or the handle may have been recycled
            // since the last enumeration. Re-read the native identity before
            // handing it back so callers never act on a stale mapping.
            var current = _api.TryGetWindow(entry.Hwnd);
            if (current is not null && entry.HasSameIdentity(current))
            {
                // Titles change routinely (document names, unread counts,
                // navigation), so enumeration-time exclusions are not enough:
                // a window enumerated as "New Tab" may now show banking. The
                // ID is invalidated rather than returned.
                if (_exclusions is not null
                    && _exclusions.IsExcluded(current.ProcessName, current.Title))
                {
                    Invalidate(entry);
                    resolved = null;
                    return false;
                }

                entry.Update(current);
                resolved = entry.ToResolvedWindow();
                return true;
            }

            Invalidate(entry);
            resolved = null;
            return false;
        }
    }

    /// <summary>Simulates the OS reporting that a window died.</summary>
    internal void MarkDeadForTests(nint hwnd)
    {
        lock (_gate)
        {
            if (_byHwnd.Remove(hwnd, out var entry))
            {
                Invalidate(entry);
            }
        }
    }

    /// <summary>Removes a dead entry so its ID never resolves again.</summary>
    private void Invalidate(Entry entry)
    {
        entry.Alive = false;
        _byId.Remove(entry.WindowId);
        if (_byHwnd.TryGetValue(entry.Hwnd, out var mapped) && ReferenceEquals(mapped, entry))
        {
            _byHwnd.Remove(entry.Hwnd);
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
