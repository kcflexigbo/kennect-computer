using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Uia;

/// <summary>
/// A live element under a physical screen point whose center is a safe
/// substitute for an estimated coordinate click. The bounds are the live
/// element's, so the click lands on the element rather than near it.
/// </summary>
public sealed record SnapTarget(string Role, string Name, Rect Bounds);

/// <summary>
/// Resolves a stored snapshot node back to the live element it was observed
/// on. Implementations re-check the element's identity and return null when
/// the fingerprint no longer matches: a missing element is a stale snapshot
/// (callers map it to <c>STALE_SNAPSHOT</c>), never an exception.
/// </summary>
public interface IUiaNodeResolver
{
    /// <param name="hwnd">Live handle of the window the snapshot was taken from.</param>
    /// <param name="target">The node captured in the snapshot.</param>
    /// <param name="cancellationToken">Aborts a slow provider lookup.</param>
    /// <returns>
    /// The matching live node, or null when the element is gone or its
    /// identity changed.
    /// </returns>
    IUiaNode? Resolve(nint hwnd, TreeModel target, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the live element under a physical screen point and returns its
    /// bounds center as a click target. Null when the point is over a
    /// container, outside the window, or the provider cannot answer; callers
    /// then keep the original coordinate.
    /// </summary>
    SnapTarget? SnapToPoint(nint hwnd, int x, int y, CancellationToken cancellationToken)
        => null;
}
