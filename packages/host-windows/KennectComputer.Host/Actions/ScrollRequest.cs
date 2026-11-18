using KennectComputer.Host.Input;

namespace KennectComputer.Host.Actions;

/// <summary>The wheel direction a scroll request asks for (DESIGN §6.6).</summary>
public enum ScrollDirection
{
    Up,

    Down,

    Left,

    Right,
}

/// <summary>
/// The host-side form of a shared <c>ScrollRequest</c> (R9). Task 13 parses the
/// protocol JSON into this record; the defaults mirror the shared schema's
/// optional fields: image-space coordinates and one wheel notch. Exactly one of
/// <see cref="NodeId"/> or (<see cref="X"/> and <see cref="Y"/>) must be
/// supplied.
/// </summary>
public sealed record ScrollRequest
{
    /// <summary>The snapshot node to scroll at; valid only for its generation.</summary>
    public string? NodeId { get; init; }

    /// <summary>The physical or image-space X, depending on <see cref="Space"/>.</summary>
    public double? X { get; init; }

    /// <summary>The physical or image-space Y, depending on <see cref="Space"/>.</summary>
    public double? Y { get; init; }

    /// <summary>The space of <see cref="X"/> and <see cref="Y"/>; image by default.</summary>
    public CoordinateSpace Space { get; init; } = CoordinateSpace.Image;

    /// <summary>
    /// The wheel direction. Left and right turn the horizontal wheel; down and
    /// left are negative deltas.
    /// </summary>
    public ScrollDirection Direction { get; init; } = ScrollDirection.Down;

    /// <summary>How many wheel notches to turn; clamped to 1..100 by the action.</summary>
    public int Amount { get; init; } = 1;

    /// <summary>Preconditions evaluated against a fresh read before any input.</summary>
    public IReadOnlyList<ExpectPredicate> Expect { get; init; } = [];
}
