using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Actions;

/// <summary>
/// The result of a mutating action (R10). Mirrors the shared protocol
/// <c>ActionResult</c>: the window that was acted on, the action kind, how it
/// was carried out, and whether the caller must observe the window again
/// before its next action.
/// </summary>
public sealed record ActionResult
{
    public WindowInfo Window { get; init; } = new();

    /// <summary>The request kind, for example <c>click</c>.</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>How the action was carried out, for example <c>pattern</c>.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>
    /// True for every mutating action: the screen may have changed, so the
    /// caller must take a fresh snapshot before acting on node IDs again.
    /// </summary>
    public bool NeedsObservation { get; init; } = true;
}
