using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;

using MapPoint = KennectComputer.Host.Input.Point;

namespace KennectComputer.Host.Tests;

/// <summary>
/// Pins coordinate mapping (DESIGN §6.2, plan failure mode 3): image-space
/// points go through the snapshot's scale and source bounds and are refused
/// after the window moves or resizes, while screen-space points are physical
/// already and only need to land inside the current window. No desktop or
/// external app is touched.
/// </summary>
public sealed class CoordinateMapperTests
{
    private static readonly Rect WindowBounds = new(100, 50, 3000, 2000);

    [Fact]
    public void ImagePointMapsThroughSourceBoundsAndScale()
    {
        // A 1568-pixel-wide returned image for a 3000-pixel-wide source:
        // scale = 3000/1568, so (10, 20) maps to
        // (100 + 10*scale, 50 + 20*scale) = (119.13, 88.27), rounded to whole
        // physical pixels.
        var state = StateWithImage(3000.0 / 1568.0, new Rect(100, 50, 3000, 2000));

        var result = CoordinateMapper.Map(state, WindowAt(WindowBounds), 10, 20, CoordinateSpace.Image);

        Assert.Null(result.Error);
        Assert.Equal(new MapPoint(119, 88), result.Point);
    }

    [Theory]
    [InlineData(1.5, 10.0, 20.0, 115, 80)]
    [InlineData(2.0, 10.0, 20.0, 120, 90)]
    [InlineData(1.5, 10.9, 20.9, 116, 81)] // 116.35 and 81.35 round down
    [InlineData(1.5, 11.0, 12.0, 117, 68)] // 116.5 rounds away from zero
    [InlineData(2.0, 10.25, 20.75, 121, 92)] // 120.5 and 91.5 round away from zero
    public void ImageScaleMapsToTheNearestPhysicalPixel(
        double scale,
        double x,
        double y,
        int expectedX,
        int expectedY)
    {
        var state = StateWithImage(scale, new Rect(100, 50, 3000, 2000));

        var result = CoordinateMapper.Map(state, WindowAt(WindowBounds), x, y, CoordinateSpace.Image);

        Assert.Null(result.Error);
        Assert.Equal(new MapPoint(expectedX, expectedY), result.Point);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("width")]
    [InlineData("height")]
    public void ImageSpaceWithAChangedWindowBoundsIsStale(string field)
    {
        var state = StateWithImage(1.5, new Rect(100, 50, 3000, 2000));

        var result = CoordinateMapper.Map(
            state,
            WindowAt(WithChangedBounds(field)),
            10,
            20,
            CoordinateSpace.Image);

        Assert.Equal(ErrorCode.StaleSnapshot, result.Error);
        Assert.Null(result.Point);
    }

    [Fact]
    public void ImageSpaceWithoutACapturedImageIsInvalid()
    {
        var state = StateWithImage(1.5, new Rect(100, 50, 3000, 2000)) with { Image = null };

        var result = CoordinateMapper.Map(state, WindowAt(WindowBounds), 10, 20, CoordinateSpace.Image);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error);
        Assert.Null(result.Point);
    }

    [Fact]
    public void ImageSpaceChecksForAnImageBeforeTheStaleBounds()
    {
        var state = StateWithImage(1.5, new Rect(100, 50, 3000, 2000)) with { Image = null };

        var result = CoordinateMapper.Map(
            state,
            WindowAt(WithChangedBounds("x")),
            10,
            20,
            CoordinateSpace.Image);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error);
        Assert.Null(result.Point);
    }

    [Fact]
    public void ScreenSpaceInsideTheWindowSucceedsEvenWhenBoundsChanged()
    {
        var state = StateWithImage(1.5, new Rect(100, 50, 3000, 2000));

        var result = CoordinateMapper.Map(
            state,
            WindowAt(WithChangedBounds("x")),
            1500,
            900,
            CoordinateSpace.Screen);

        Assert.Null(result.Error);
        Assert.Equal(new MapPoint(1500, 900), result.Point);
    }

    [Fact]
    public void ScreenSpaceNeedsNoCapturedImage()
    {
        var state = StateWithImage(1.5, new Rect(100, 50, 3000, 2000)) with { Image = null };

        var result = CoordinateMapper.Map(state, WindowAt(WindowBounds), 1500, 900, CoordinateSpace.Screen);

        Assert.Null(result.Error);
        Assert.Equal(new MapPoint(1500, 900), result.Point);
    }

    [Fact]
    public void ScreenSpaceAcceptsTheTopLeftWindowPixel()
    {
        var state = StateWithImage(1.5, new Rect(100, 50, 3000, 2000));

        var result = CoordinateMapper.Map(state, WindowAt(WindowBounds), 100, 50, CoordinateSpace.Screen);

        Assert.Null(result.Error);
        Assert.Equal(new MapPoint(100, 50), result.Point);
    }

    [Fact]
    public void AnUnknownCoordinateSpaceIsInvalid()
    {
        var state = StateWithImage(1.5, new Rect(100, 50, 3000, 2000));

        var result = CoordinateMapper.Map(
            state,
            WindowAt(WindowBounds),
            10,
            20,
            (CoordinateSpace)42);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error);
        Assert.Null(result.Point);
    }

    [Theory]
    [InlineData(50, 900)] // left of the window
    [InlineData(1500, 30)] // above the window
    [InlineData(3500, 900)] // right of the window
    [InlineData(1500, 2500)] // below the window
    public void ScreenSpaceOutsideTheWindowIsInvalid(double x, double y)
    {
        var state = StateWithImage(1.5, new Rect(100, 50, 3000, 2000));

        var result = CoordinateMapper.Map(state, WindowAt(WindowBounds), x, y, CoordinateSpace.Screen);

        Assert.Equal(ErrorCode.InvalidRequest, result.Error);
        Assert.Null(result.Point);
    }

    private static SnapshotState StateWithImage(double scale, Rect sourceBounds) => new(
        Generation: 1,
        Nodes: new Dictionary<string, TreeModel>(StringComparer.Ordinal),
        WindowBounds: WindowBounds,
        Image: new ImageMetadata(scale, sourceBounds));

    private static WindowInfo WindowAt(Rect bounds) => new()
    {
        WindowId = "w_1",
        Title = "Test window",
        ProcessName = "test",
        Bounds = bounds,
    };

    private static Rect WithChangedBounds(string field) => field switch
    {
        "x" => new Rect(WindowBounds.X + 1, WindowBounds.Y, WindowBounds.Width, WindowBounds.Height),
        "y" => new Rect(WindowBounds.X, WindowBounds.Y + 1, WindowBounds.Width, WindowBounds.Height),
        "width" => new Rect(WindowBounds.X, WindowBounds.Y, WindowBounds.Width + 1, WindowBounds.Height),
        "height" => new Rect(WindowBounds.X, WindowBounds.Y, WindowBounds.Width, WindowBounds.Height + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown bounds field."),
    };
}
