using System.Globalization;

namespace KennectComputer.Host.Uia;

/// <summary>
/// Ephemeral node IDs of the form <c>n&lt;generation&gt;:&lt;index&gt;</c>. The
/// generation is part of the ID so a node from an older snapshot is
/// distinguishable (and rejectable) without a separate lookup table.
/// </summary>
public static class NodeId
{
    public static string Create(int generation, int index)
        => "n"
            + generation.ToString(CultureInfo.InvariantCulture)
            + ":"
            + index.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? nodeId, out int generation, out int index)
    {
        generation = -1;
        index = -1;

        if (string.IsNullOrEmpty(nodeId) || nodeId.Length < 4 || nodeId[0] != 'n')
        {
            return false;
        }

        var separator = nodeId.IndexOf(':');
        if (separator <= 1 || separator == nodeId.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(
                nodeId.AsSpan(1, separator - 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out generation)
            || generation < 0)
        {
            return false;
        }

        if (!int.TryParse(
                nodeId.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out index)
            || index < 0)
        {
            return false;
        }

        return true;
    }
}
