using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Uia;

/// <summary>
/// Keeps the latest snapshot state per window so later actions can resolve a
/// node ID back to the element it was observed on, and can map coordinates
/// using the bounds and image scale from that same snapshot. IDs from an older
/// generation are rejected, because their element may now be a different
/// control.
///
/// Pipe requests may interleave, so every operation takes the same gate.
/// </summary>
public sealed class SnapshotStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SnapshotState> _byWindow = new(StringComparer.Ordinal);

    public void Save(
        string windowId,
        Snapshot snapshot,
        IReadOnlyDictionary<string, TreeModel> nodeMap,
        Rect windowBounds)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(nodeMap);
        ArgumentNullException.ThrowIfNull(windowBounds);

        var image = snapshot.Image is { } payload
            ? new ImageMetadata(payload.Scale, payload.SourceBounds)
            : null;

        var state = new SnapshotState(snapshot.Generation, nodeMap, windowBounds, image);

        lock (_gate)
        {
            _byWindow[windowId] = state;
        }
    }

    /// <summary>
    /// Returns the latest state for the window. Callers must still compare the
    /// state's generation with the generation embedded in a node ID.
    /// </summary>
    public bool TryGetState(string windowId, out SnapshotState? state)
    {
        state = null;

        if (string.IsNullOrEmpty(windowId))
        {
            return false;
        }

        lock (_gate)
        {
            return _byWindow.TryGetValue(windowId, out state);
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
            if (!_byWindow.TryGetValue(windowId, out var state))
            {
                return false;
            }

            if (!NodeId.TryParse(nodeId, out var generation, out _)
                || generation != state.Generation)
            {
                return false;
            }

            return state.Nodes.TryGetValue(nodeId, out node);
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
}
