using KennectComputer.Host.Capture;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;

namespace KennectComputer.Host.Tests;

public sealed class ScreenCaptureTests
{
    [Fact]
    public void DownscalesToTheLongEdgeAndKeepsAspect()
    {
        using var bitmap = new Bitmap(3000, 2000);

        var payload = ScreenCapture.Encode(
            bitmap,
            imageLongEdge: 1568,
            sourceBounds: new Rect(0, 0, 3000, 2000));

        using var image = new Bitmap(new MemoryStream(Convert.FromBase64String(payload.Data)));
        Assert.Equal(1568, Math.Max(image.Width, image.Height));
        Assert.Equal(3000.0 / 1568.0, payload.Scale, 2);
        Assert.Equal(2000.0 / 3000.0, image.Height / (double)image.Width, 2);
        Assert.Equal("image/png", payload.MimeType);
    }

    [Fact]
    public void DoesNotUpscaleAnImageThatFitsWithinTheLongEdge()
    {
        using var bitmap = new Bitmap(400, 200);

        var payload = ScreenCapture.Encode(bitmap, 1568, new Rect(10, 20, 400, 200));

        using var image = new Bitmap(new MemoryStream(Convert.FromBase64String(payload.Data)));
        Assert.Equal(400, image.Width);
        Assert.Equal(200, image.Height);
        Assert.Equal(1.0, payload.Scale, 3);
        Assert.Equal(10, payload.SourceBounds.X);
        Assert.Equal(20, payload.SourceBounds.Y);
        Assert.Equal(400, payload.SourceBounds.Width);
        Assert.Equal(200, payload.SourceBounds.Height);
    }

    [Fact]
    public void ClampsTheLongEdgeToTheProtocolMaximum()
    {
        using var bitmap = new Bitmap(4000, 2000);

        var payload = ScreenCapture.Encode(
            bitmap,
            imageLongEdge: 10_000,
            sourceBounds: new Rect(0, 0, 4000, 2000));

        using var image = new Bitmap(new MemoryStream(Convert.FromBase64String(payload.Data)));
        Assert.Equal(SnapshotOptions.DefaultMaxImageLongEdge, Math.Max(image.Width, image.Height));
        Assert.Equal(4000.0 / SnapshotOptions.DefaultMaxImageLongEdge, payload.Scale, 2);
    }

    [Fact]
    public void OmitsTheImageWhenNotRequested()
    {
        var payload = ScreenCapture.Capture(
            hwnd: (nint)1,
            bounds: new Rect(0, 0, 100, 100),
            imageLongEdge: 1568,
            enabled: false);

        Assert.Null(payload);
    }

    [Fact]
    public void ReturnsNullForBoundsThatCannotBeCaptured()
    {
        Assert.Null(ScreenCapture.Capture((nint)1, new Rect(0, 0, 0, 0), 1568));
        Assert.Null(ScreenCapture.Capture((nint)1, new Rect(0, 0, 100, 0), 1568));
        Assert.Null(ScreenCapture.Capture((nint)1, new Rect(0, 0, 0, 100), 1568));
        Assert.Null(ScreenCapture.Capture(nint.Zero, new Rect(0, 0, 100, 100), 1568));
    }

    [Fact]
    public void CapturesAnOwnedWindowAtItsBounds()
    {
        // The test owns this window and closes it, so the run never launches an
        // external app or touches a user-owned window. The real capture path
        // (PrintWindow, with the CopyFromScreen fallback) runs here.
        using var window = OwnedTestWindow.Start();
        var bounds = window.Bounds;

        var payload = ScreenCapture.Capture(window.Handle, bounds, SnapshotOptions.DefaultImageLongEdge);

        Assert.NotNull(payload);
        Assert.Equal("image/png", payload!.MimeType);
        Assert.Equal(bounds.X, payload.SourceBounds.X);
        Assert.Equal(bounds.Y, payload.SourceBounds.Y);
        Assert.Equal(bounds.Width, payload.SourceBounds.Width);
        Assert.Equal(bounds.Height, payload.SourceBounds.Height);

        using var image = new Bitmap(new MemoryStream(Convert.FromBase64String(payload.Data)));
        Assert.True(
            Math.Max(image.Width, image.Height) <= SnapshotOptions.DefaultImageLongEdge,
            $"Captured image {image.Width}x{image.Height} exceeds the long edge.");
        Assert.Equal(bounds.Height / (double)bounds.Width, image.Height / (double)image.Width, 2);
        Assert.Equal(bounds.Width / (double)image.Width, payload.Scale, 3);
    }
}
