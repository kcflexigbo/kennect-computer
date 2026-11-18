using KennectComputer.Host.Policy;
using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Windows;

/// <summary>
/// Lists user-facing top-level windows. Invisible, DWM-cloaked, tool, and
/// untitled windows are filtered out, as are the host's own windows and
/// windows excluded by the privacy policy, before an opaque ID is assigned, so
/// they can never be returned or selected through a stale ID.
/// </summary>
public sealed class WindowEnumerator
{
    private const long WsExToolWindow = 0x00000080;

    private readonly IWindowApi _api;
    private readonly WindowRegistry _registry;
    private readonly IExclusionPolicy? _exclusions;

    public WindowEnumerator(
        IWindowApi api,
        WindowRegistry registry,
        IExclusionPolicy? exclusions = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _exclusions = exclusions;
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

            // R34: excluded windows are dropped before registration so they
            // never receive an ID that could later resolve.
            if (_exclusions is not null
                && _exclusions.IsExcluded(window.ProcessName, window.Title))
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
            && !string.IsNullOrWhiteSpace(window.ProcessName)
            // The per-app authorization dialog is a normal titled, visible
            // window owned by the host. DESIGN §8 hard rule 3 excludes the
            // host's own dialogs unconditionally, not just by config.
            && window.Pid != Environment.ProcessId;
}
