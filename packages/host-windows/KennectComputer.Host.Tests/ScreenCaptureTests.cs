using System.Drawing.Imaging;
using System.Runtime.InteropServices;

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
    public void ACropReturnsNativePixelsAndTheCropRect()
    {
        using var bitmap = new Bitmap(100, 80, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(255, 10, 20, 30));
        }

        var payload = ScreenCapture.EncodeRegion(
            bitmap,
            new Rect(0, 0, 100, 80),
            new Rect(10, 20, 30, 40));

        Assert.NotNull(payload);
        Assert.Equal(1.0, payload!.Scale, 3);
        Assert.Equal(10, payload.SourceBounds.X);
        Assert.Equal(20, payload.SourceBounds.Y);
        Assert.Equal(30, payload.SourceBounds.Width);
        Assert.Equal(40, payload.SourceBounds.Height);

        using var image = new Bitmap(new MemoryStream(Convert.FromBase64String(payload.Data)));
        Assert.Equal(30, image.Width);
        Assert.Equal(40, image.Height);

        var pixel = image.GetPixel(15, 20);
        Assert.Equal(10, pixel.R);
        Assert.Equal(20, pixel.G);
        Assert.Equal(30, pixel.B);
    }

    [Fact]
    public void ACropOutsideTheBitmapReturnsNull()
    {
        using var bitmap = new Bitmap(100, 80);

        Assert.Null(ScreenCapture.EncodeRegion(
            bitmap, new Rect(0, 0, 100, 80), new Rect(90, 70, 20, 20)));
        Assert.Null(ScreenCapture.EncodeRegion(
            bitmap, new Rect(0, 0, 100, 80), new Rect(-1, 0, 10, 10)));
    }

    [Fact]
    public void EncodePreservesContentFromAZeroAlphaCapture()
    {
        // GDI can hand back screen content with a zero alpha channel even
        // though the RGB pixels are present. Downscaling must not blend that
        // content away into a blank, fully transparent PNG.
        using var bitmap = NewZeroAlphaBitmap(2000, 1000, Color.FromArgb(200, 30, 40));

        var payload = ScreenCapture.Encode(bitmap, 1568, new Rect(0, 0, 2000, 1000));

        using var image = new Bitmap(new MemoryStream(Convert.FromBase64String(payload.Data)));
        Assert.Equal(1568, Math.Max(image.Width, image.Height));
        Assert.True(
            ContainsVisiblePixel(image),
            "The encoded image is fully transparent or blank.");

        var center = image.GetPixel(image.Width / 2, image.Height / 2);
        Assert.Equal(255, center.A);
        Assert.Equal(200, center.R);
        Assert.Equal(30, center.G);
        Assert.Equal(40, center.B);
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

        // The capture must carry actual rendered pixels, not a transparent or
        // blank image that merely happens to decode to the right dimensions.
        Assert.True(
            ContainsVisiblePixel(image),
            "The captured image is fully transparent or blank.");
    }

    private static Bitmap NewZeroAlphaBitmap(int width, int height, Color color)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * height];
            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                bytes[offset] = color.B;
                bytes[offset + 1] = color.G;
                bytes[offset + 2] = color.R;
                bytes[offset + 3] = 0;
            }

            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    /// <summary>True when the decoded image has at least one opaque, non-black pixel.</summary>
    private static bool ContainsVisiblePixel(Bitmap image)
    {
        var data = image.LockBits(
            new Rectangle(0, 0, image.Width, image.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);

            for (var y = 0; y < data.Height; y++)
            {
                var row = y * data.Stride;
                for (var x = 0; x < data.Width; x++)
                {
                    var offset = row + (x * 4);
                    var hasRgb = bytes[offset] != 0
                        || bytes[offset + 1] != 0
                        || bytes[offset + 2] != 0;

                    if (hasRgb && bytes[offset + 3] != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            image.UnlockBits(data);
        }
    }
}
