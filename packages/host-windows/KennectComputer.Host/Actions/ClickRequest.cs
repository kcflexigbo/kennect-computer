using KennectComputer.Host.Input;

namespace KennectComputer.Host.Actions;

/// <summary>
/// The host-side form of a shared <c>ClickRequest</c> (R9). Task 13 parses the
/// protocol JSON into this record; the defaults mirror the shared schema:
/// image-space coordinates, the left button, and a single click. Exactly one
/// of <see cref="NodeId"/>, (<see cref="X"/> and <see cref="Y"/>), or
/// <see cref="Text"/> must be supplied.
/// </summary>
public sealed record ClickRequest
{
    /// <summary>The snapshot node to click; valid only for its generation.</summary>
    public string? NodeId { get; init; }

    /// <summary>The physical or image-space X, depending on <see cref="Space"/>.</summary>
    public double? X { get; init; }

    /// <summary>The physical or image-space Y, depending on <see cref="Space"/>.</summary>
    public double? Y { get; init; }

    /// <summary>
    /// Case-insensitive substring of a live element's accessible name. The
    /// element is looked up at action time; exactly one match is required.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>Exact lower-case role matched together with <see cref="Text"/>.</summary>
    public string? Role { get; init; }

    /// <summary>The space of <see cref="X"/> and <see cref="Y"/>; image by default.</summary>
    public CoordinateSpace Space { get; init; } = CoordinateSpace.Image;

    public MouseButton Button { get; init; } = MouseButton.Left;

    /// <summary>How many clicks to send; must be at least one.</summary>
    public int Count { get; init; } = 1;

    /// <summary>
    /// Modifier names held around the click (Ctrl, Shift, Alt, Win), pressed in
    /// order and released in reverse. Any modifier, a non-left button, or a
    /// count other than one makes the click a mouse gesture rather than a
    /// semantic pattern activation.
    /// </summary>
    public IReadOnlyList<string> Modifiers { get; init; } = [];

    /// <summary>Preconditions evaluated against a fresh read before any input.</summary>
    public IReadOnlyList<ExpectPredicate> Expect { get; init; } = [];
}
