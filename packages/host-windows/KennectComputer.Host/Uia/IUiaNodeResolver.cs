namespace KennectComputer.Host.Uia;

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
}
