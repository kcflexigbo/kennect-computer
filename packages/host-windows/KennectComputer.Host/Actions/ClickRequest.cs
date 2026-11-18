using KennectComputer.Host.Input;

namespace KennectComputer.Host.Actions;

/// <summary>
/// The host-side form of a shared <c>ClickRequest</c> (R9). Task 13 parses the
/// protocol JSON into this record; the defaults mirror the shared schema:
/// image-space coordinates, the left button, and a single click. Exactly one
/// of <see cref="NodeId"/> or (<see cref="X"/> and <see cref="Y"/>) must be
/// supplied.
/// </summary>
public sealed record ClickRequest
{
    /// <summary>The snapshot node to click; valid only for its generation.</summary>
    public string? NodeId { get; init; }

    /// <summary>The physical or image-space X, depending on <see cref="Space"/>.</summary>
    public double? X { get; init; }

    /// <summary>The physical or image-space Y, depending on <see cref="Space"/>.</summary>
    public double? Y { get; init; }

    /// <summary>The space of <see cref="X"/> and <see cref="Y"/>; image by default.</summary>
    public CoordinateSpace Space { get; init; } = CoordinateSpace.Image;

    public MouseButton Button { get; init; } = MouseButton.Left;

    /// <summary>How many clicks to send; must be at least one.</summary>
    public int Count { get; init; } = 1;

    /// <summary>Preconditions evaluated against a fresh read before any input.</summary>
    public IReadOnlyList<ExpectPredicate> Expect { get; init; } = [];
}
