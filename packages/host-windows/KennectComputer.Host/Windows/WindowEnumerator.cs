using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Windows;

/// <summary>
/// Lists user-facing top-level windows. Invisible, DWM-cloaked, tool, and
/// untitled windows are filtered out before an opaque ID is assigned, so they
/// can never be returned or selected through a stale ID.
/// </summary>
public sealed class WindowEnumerator
{
    private const long WsExToolWindow = 0x00000080;

    private readonly IWindowApi _api;
    private readonly WindowRegistry _registry;

    public WindowEnumerator(IWindowApi api, WindowRegistry registry)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public IReadOnlyList<WindowInfo> List()
    {
        var windows = new List<WindowInfo>();
        foreach (var window in _api.GetTopLevelWindows())
        {
            if (!IsListable(window))
            {
                continue;
            }

            windows.Add(_registry.Register(window));
        }

        return windows;
    }

    private static bool IsListable(RawWindow window)
        => window.Visible
            && !window.Cloaked
            && (window.ExStyle & WsExToolWindow) == 0
            && !string.IsNullOrWhiteSpace(window.Title)
            && !string.IsNullOrWhiteSpace(window.ProcessName);
}
