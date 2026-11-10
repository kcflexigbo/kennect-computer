using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Windows;

/// <summary>Supplies the display topology reported by <c>status</c>.</summary>
public interface IMonitorProvider
{
    IReadOnlyList<MonitorInfo> GetMonitors();
}
