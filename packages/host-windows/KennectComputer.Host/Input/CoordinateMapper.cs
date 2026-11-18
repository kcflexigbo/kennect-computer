using KennectComputer.Host.Control;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;

namespace KennectComputer.Host.Input;

/// <summary>The space an action's <c>x</c>/<c>y</c> pair is expressed in (DESIGN §6.2).</summary>
public enum CoordinateSpace
{
    /// <summary>Pixels of the last snapshot's returned (scaled) image.</summary>
    Image,

    /// <summary>Absolute physical screen pixels.</summary>
    Screen,
}

/// <summary>A whole-pixel point in physical screen coordinates.</summary>
public readonly record struct Point(int X, int Y);

/// <summary>
/// The outcome of mapping action coordinates: either the physical point to act
/// on or the error code that made the mapping unsafe.
/// </summary>
public sealed class CoordinateMapResult
{
    public Point? Point { get; init; }

    public ErrorCode? Error { get; init; }

    public static CoordinateMapResult Success(Point point) => new() { Point = point };

    public static CoordinateMapResult Failed(ErrorCode error) => new() { Error = error };
}

/// <summary>
/// Maps action coordinates to physical screen pixels (DESIGN §6.2).
///
/// Image space is relative to the last snapshot's returned image, so the
/// snapshot must carry the image's scale and source bounds, the point must lie
/// inside the returned image's pixel extent, and the window must not have moved
/// or resized since the snapshot; otherwise the mapping would silently click
/// the wrong place. Screen space is already physical, so only the window
/// containment check remains and a moved window is still addressable.
///
/// Mapped values are rounded to the nearest whole physical pixel, with midpoints
/// away from zero, and the window rectangle is treated as half-open in pixels:
/// <c>[X, X+Width) × [Y, Y+Height)</c>.
/// </summary>
public static class CoordinateMapper
{
    public static CoordinateMapResult Map(
        SnapshotState state,
        WindowInfo currentWindow,
        double x,
        double y,
        CoordinateSpace space)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(currentWindow);

        return space switch
        {
            CoordinateSpace.Image => MapFromImage(state, currentWindow, x, y),
            CoordinateSpace.Screen => MapFromScreen(currentWindow, x, y),
            _ => CoordinateMapResult.Failed(ErrorCode.InvalidRequest),
        };
    }

    private static CoordinateMapResult MapFromImage(
        SnapshotState state,
        WindowInfo currentWindow,
        double x,
        double y)
    {
        var image = state.Image;
        if (image is null)
        {
            return CoordinateMapResult.Failed(ErrorCode.InvalidRequest);
        }

        if (!BoundsEqual(currentWindow.Bounds, state.WindowBounds))
        {
            return CoordinateMapResult.Failed(ErrorCode.StaleSnapshot);
        }

        // Scale is physical pixels per returned image pixel, so the returned
        // image's extent is the physical source size divided by the scale. A
        // point outside it was never visible in the image the caller acted on;
        // rejecting it here also keeps far-out values from overflowing the int
        // conversion below into an unrelated physical point.
        if (!WithinImage(x, image.SourceBounds.Width / image.Scale)
            || !WithinImage(y, image.SourceBounds.Height / image.Scale))
        {
            return CoordinateMapResult.Failed(ErrorCode.InvalidRequest);
        }

        return CoordinateMapResult.Success(new Point(
            ToPhysicalPixel(image.SourceBounds.X + (x * image.Scale)),
            ToPhysicalPixel(image.SourceBounds.Y + (y * image.Scale))));
    }

    private static CoordinateMapResult MapFromScreen(WindowInfo currentWindow, double x, double y)
    {
        var bounds = currentWindow.Bounds;
        if (x < bounds.X
            || x >= bounds.X + bounds.Width
            || y < bounds.Y
            || y >= bounds.Y + bounds.Height)
        {
            return CoordinateMapResult.Failed(ErrorCode.InvalidRequest);
        }

        return CoordinateMapResult.Success(new Point(
            ToPhysicalPixel(x),
            ToPhysicalPixel(y)));
    }

    /// <summary>
    /// Half-open <c>[0, extent)</c> containment of an image-pixel coordinate.
    /// The ordered comparisons reject NaN too, so a non-finite value can never
    /// slip through the extent check.
    /// </summary>
    private static bool WithinImage(double value, double extent)
        => value >= 0 && value < extent;

    // Rect is a mutable protocol class with reference equality, so the window
    // has moved unless all four whole-pixel fields still match.
    private static bool BoundsEqual(Rect left, Rect right)
        => left.X == right.X
            && left.Y == right.Y
            && left.Width == right.Width
            && left.Height == right.Height;

    private static int ToPhysicalPixel(double value)
        => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
