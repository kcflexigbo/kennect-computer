using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Uia;

/// <summary>
/// The identity a snapshot node had when it was captured: generic role,
/// accessible name, platform identifier (when the app exposes one), and whole-
/// pixel bounds. Every field must match for an action to run; a title change
/// is irrelevant, while moving, resizing, renaming, or replacing the element
/// invalidates it.
///
/// Bounds are hashed rather than carried as four ints because the whole set is
/// compared as one identity component. <see cref="Rect"/> is whole-pixel by
/// construction, so the hash is the whole-pixel bounds identity.
/// </summary>
public readonly record struct NodeFingerprint(
    string Role,
    string Name,
    string? Identifier,
    int BoundsHash)
{
    public static NodeFingerprint From(TreeModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return new NodeFingerprint(
            node.Role,
            node.Name,
            NormalizeIdentifier(node.Identifier),
            ComputeBoundsHash(node.Bounds));
    }

    public bool Matches(TreeModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return string.Equals(Role, node.Role, StringComparison.Ordinal)
            && string.Equals(Name, node.Name, StringComparison.Ordinal)
            && string.Equals(Identifier, NormalizeIdentifier(node.Identifier), StringComparison.Ordinal)
            && BoundsHash == ComputeBoundsHash(node.Bounds);
    }

    /// <summary>An empty identifier and a missing identifier are the same identity.</summary>
    private static string? NormalizeIdentifier(string? identifier)
        => string.IsNullOrEmpty(identifier) ? null : identifier;

    private static int ComputeBoundsHash(Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + bounds.X;
            hash = (hash * 31) + bounds.Y;
            hash = (hash * 31) + bounds.Width;
            hash = (hash * 31) + bounds.Height;
            return hash;
        }
    }
}
