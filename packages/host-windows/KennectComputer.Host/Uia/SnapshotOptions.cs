namespace KennectComputer.Host.Uia;

/// <summary>Which perception layers a snapshot should produce.</summary>
public enum SnapshotMode
{
    A11y,
    Vision,
    Both,
}

/// <summary>
/// Bounds and defaults for one snapshot (DESIGN §5 and the plan's global
/// constraints). Image capture itself arrives with Task 9; the flags are
/// carried now so the protocol shape does not change later.
/// </summary>
public sealed record SnapshotOptions
{
    public const int DefaultMaxDepth = 12;
    public const int DefaultMaxNodes = 1500;
    public const int DefaultImageLongEdge = 1568;
    public const int DefaultMaxImageLongEdge = 2576;

    /// <summary>
    /// Web apps expose their document content far below the generic depth cap
    /// (R7/R11); a Document subtree is walked to at least this depth.
    /// </summary>
    public const int DocumentMaxDepth = 60;

    public int MaxDepth { get; init; } = DefaultMaxDepth;

    public int MaxNodes { get; init; } = DefaultMaxNodes;

    public bool Image { get; init; }

    public int ImageLongEdge { get; init; } = DefaultImageLongEdge;

    public int MaxImageLongEdge { get; init; } = DefaultMaxImageLongEdge;

    public SnapshotMode Mode { get; init; } = SnapshotMode.Both;
}
