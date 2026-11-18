using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Uia;

/// <summary>
/// The parts of a snapshot image that coordinate mapping needs. Actions never
/// read the base64 data, so the host keeps only the scale and source bounds.
/// </summary>
public sealed record ImageMetadata(double Scale, Rect SourceBounds);

/// <summary>
/// The latest snapshot for one window, in the form later actions consume:
/// node IDs are valid only for <see cref="Generation"/>, the window bounds are
/// where the window was when the snapshot was taken, and <see cref="Image"/>
/// is present only when an image was captured.
/// </summary>
public sealed record SnapshotState(
    int Generation,
    IReadOnlyDictionary<string, TreeModel> Nodes,
    Rect WindowBounds,
    ImageMetadata? Image);
