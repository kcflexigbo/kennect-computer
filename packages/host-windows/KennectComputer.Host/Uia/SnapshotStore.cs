using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Uia;

/// <summary>
/// Keeps the latest node map per window so later actions can resolve a node ID
/// back to the element it was observed on. IDs from an older generation are
/// rejected, because their element may now be a different control.
///
/// Pipe requests may interleave, so every operation takes the same gate.
/// </summary>
public sealed class SnapshotStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byWindow = new(StringComparer.Ordinal);

    public void Save(
        string windowId,
        Snapshot snapshot,
        IReadOnlyDictionary<string, TreeModel> nodeMap)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(nodeMap);

        lock (_gate)
        {
            _byWindow[windowId] = new Entry(snapshot.Generation, nodeMap);
        }
    }

    public bool TryGetNode(string windowId, string? nodeId, out TreeModel? node)
    {
        node = null;

        if (string.IsNullOrEmpty(windowId) || string.IsNullOrEmpty(nodeId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_byWindow.TryGetValue(windowId, out var entry))
            {
                return false;
            }

            if (!NodeId.TryParse(nodeId, out var generation, out _)
                || generation != entry.Generation)
            {
                return false;
            }

            return entry.Nodes.TryGetValue(nodeId, out node);
        }
    }

    public void Clear(string windowId)
    {
        if (string.IsNullOrEmpty(windowId))
        {
            return;
        }

        lock (_gate)
        {
            _byWindow.Remove(windowId);
        }
    }

    private sealed record Entry(int Generation, IReadOnlyDictionary<string, TreeModel> Nodes);
}
