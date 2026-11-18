using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;

namespace KennectComputer.Host.Actions;

/// <summary>
/// Resolves coordinate and live-bounds targets shared by the click and scroll
/// actions (DESIGN §6.2). The mapping contract, the image-space snapshot guard,
/// the screen-only workaround, and the failure messages live here once, so a
/// change to any of them cannot drift between actions.
///
/// Image space is relative to the last snapshot's returned image, so a stored
/// snapshot with an image and an unmoved window are required; screen space is
/// already physical and reads only the current window. Every failure is raised
/// as an <see cref="ActionFailedException"/> carrying the protocol code.
/// </summary>
internal static class CoordinateTarget
{
    /// <summary>
    /// Maps a coordinate target to a physical screen pixel.
    /// <paramref name="state"/> is the window's latest snapshot state, or null
    /// when none was taken; <paramref name="actionName"/> ("click" or "scroll")
    /// completes the caller-facing messages.
    /// </summary>
    public static Input.Point Resolve(
        SnapshotState? state,
        WindowInfo window,
        double x,
        double y,
        CoordinateSpace space,
        string actionName)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrEmpty(actionName);

        if (space == CoordinateSpace.Image && state is null)
        {
            throw new ActionFailedException(
                ErrorCode.InvalidRequest,
                $"An image-space {actionName} requires a snapshot with an image; "
                + "snapshot the window with image enabled.");
        }

        // Screen space is physical and independent of the snapshot; the
        // mapper's screen path reads only the current window, so an empty
        // state stands in when no snapshot has been taken.
        var mapping = CoordinateMapper.Map(
            state ?? ScreenOnlyState(window),
            window,
            x,
            y,
            space);

        if (mapping.Error is { } error)
        {
            throw new ActionFailedException(error, CoordinateErrorMessage(error, actionName));
        }

        return mapping.Point
            ?? throw new ActionFailedException(
                ErrorCode.InvalidRequest,
                $"The {actionName} coordinates could not be mapped.");
    }

    /// <summary>
    /// The live element's bounds center, verified to be a point the action may
    /// safely click or wheel at. An element with no on-screen area, or whose
    /// center lies outside the verified-foreground window, is
    /// <c>STALE_SNAPSHOT</c>: acting there could hit another window. This is the
    /// node fallback's equivalent of the coordinate path's containment check.
    /// </summary>
    public static Input.Point ResolveNodeCenter(IUiaNode node, WindowInfo window, string actionName)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrEmpty(actionName);

        if (TryBoundsCenter(node.Bounds, window, out var center))
        {
            return center;
        }

        throw new ActionFailedException(
            ErrorCode.StaleSnapshot,
            node.Bounds.Width <= 0 || node.Bounds.Height <= 0
                ? $"The {actionName} target has no on-screen bounds; take a new snapshot."
                : $"The {actionName} target is not inside the selected window; "
                    + "take a new snapshot.");
    }

    /// <summary>
    /// The bounds center when the bounds have area and the center lies inside
    /// the selected window. Snapping uses this to decide whether an element
    /// found under an estimated point is a safe replacement for it.
    /// </summary>
    public static bool TryBoundsCenter(Rect bounds, WindowInfo window, out Input.Point center)
    {
        ArgumentNullException.ThrowIfNull(window);

        center = default;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var candidate = BoundsCenter(bounds);
        if (!Contains(window.Bounds, candidate))
        {
            return false;
        }

        center = candidate;
        return true;
    }

    /// <summary>The live element's bounds center in physical screen pixels.</summary>
    private static Input.Point BoundsCenter(Rect bounds) => new(
        bounds.X + (bounds.Width / 2),
        bounds.Y + (bounds.Height / 2));

    // Half-open in whole pixels, matching CoordinateMapper's window check.
    private static bool Contains(Rect bounds, Input.Point point)
        => point.X >= bounds.X
            && point.X < bounds.X + bounds.Width
            && point.Y >= bounds.Y
            && point.Y < bounds.Y + bounds.Height;

    private static string CoordinateErrorMessage(ErrorCode error, string actionName) => error switch
    {
        ErrorCode.StaleSnapshot =>
            "The window moved or resized since the snapshot; "
            + "take a new snapshot or use screen coordinates.",
        ErrorCode.InvalidRequest =>
            "The coordinates are outside the target window or the snapshot has no image.",
        _ => $"The {actionName} coordinates could not be mapped ({error.ToProtocolCode()}).",
    };

    /// <summary>
    /// A stand-in state for screen-space mapping when no snapshot exists. Only
    /// the current window reaches the mapper's screen path; the snapshot part
    /// of the state is never read.
    /// </summary>
    private static SnapshotState ScreenOnlyState(WindowInfo window) => new(
        Generation: 0,
        Nodes: new Dictionary<string, TreeModel>(StringComparer.Ordinal),
        WindowBounds: window.Bounds,
        Image: null);
}
