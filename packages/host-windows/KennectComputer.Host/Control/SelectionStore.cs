using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Control;

/// <summary>
/// The explicit session target. Actions resolve their window through this
/// store, so a window that died is never silently replaced by another one
/// (DESIGN §7).
/// </summary>
public sealed class SelectionStore
{
    private readonly object _gate = new();
    private WindowInfo? _selected;

    public WindowInfo? Selected
    {
        get
        {
            lock (_gate)
            {
                return _selected;
            }
        }
    }

    public void Select(WindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (_gate)
        {
            _selected = window;
        }
    }

    /// <summary>Forgets the target, for example after its window died.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _selected = null;
        }
    }
}
