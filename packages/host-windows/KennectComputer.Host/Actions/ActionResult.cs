using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Actions;

/// <summary>The live element a coordinate click was snapped to, if any.</summary>
public sealed record SnapInfo(string Role, string Name);

/// <summary>
/// The result of a mutating action (R10). Mirrors the shared protocol
/// <c>ActionResult</c>: the window that was acted on, the action kind, how it
/// was carried out, whether a coordinate click snapped to an element, and
/// whether the caller must observe the window again before its next action.
/// </summary>
public sealed record ActionResult
{
    public WindowInfo Window { get; init; } = new();

    /// <summary>The request kind, for example <c>click</c>.</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>How the action was carried out, for example <c>pattern</c>.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>
    /// Present when a coordinate click was corrected to the live element under
    /// the estimated point; the click then landed on that element's center.
    /// </summary>
    public SnapInfo? SnappedTo { get; init; }

    /// <summary>
    /// True for every mutating action: the screen may have changed, so the
    /// caller must take a fresh snapshot before acting on node IDs again.
    /// </summary>
    public bool NeedsObservation { get; init; } = true;
}
