using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;

namespace KennectComputer.Host.Capture;

/// <summary>
/// Captures a window as a downscaled PNG for a snapshot.
///
/// The window is rendered with <c>PrintWindow(PW_RENDERFULLCONTENT)</c> first so
/// occluded windows still capture. When that fails or yields an all-black
/// bitmap (some apps do while minimized), the window is restored and the
/// on-screen pixels over its bounds are copied instead. Every failure returns
/// null: an unavailable image never fails the accessibility snapshot.
/// </summary>
public static class ScreenCapture
{
    private const uint PwRenderFullContent = 0x00000002;
    private const int SwRestore = 9;
    private const int RestoreSettleMilliseconds = 150;

    /// <summary>
    /// Forces output alpha to 1 while leaving RGB unchanged. GDI-rendered
    /// captures can contain real RGB content with a zero alpha channel;
    /// blending such a source with the default SourceOver mode erases it.
    /// </summary>
    private static readonly ColorMatrix OpaqueAlphaMatrix = new(
        new[]
        {
            new[] { 1f, 0f, 0f, 0f, 0f },
            new[] { 0f, 1f, 0f, 0f, 0f },
            new[] { 0f, 0f, 1f, 0f, 0f },
            new[] { 0f, 0f, 0f, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
        });

    /// <summary>
    /// Captures a window's image, or null when capture is disabled or fails.
    /// <paramref name="bounds"/> are the window's physical screen pixels and
    /// become the payload's <c>sourceBounds</c>.
    /// </summary>
    public static ImagePayload? Capture(
        nint hwnd,
        Rect bounds,
        int imageLongEdge = SnapshotOptions.DefaultImageLongEdge,
        bool enabled = true,
        int maxImageLongEdge = SnapshotOptions.DefaultMaxImageLongEdge,
        Rect? crop = null)
    {
        if (!enabled
            || hwnd == nint.Zero
            || bounds is null
            || bounds.Width <= 0
            || bounds.Height <= 0)
        {
            return null;
        }

        try
        {
            using var bitmap = TryPrintWindow(hwnd, bounds) ?? TryCopyFromScreen(hwnd, bounds);
            if (bitmap is null)
            {
                return null;
            }

            return crop is null
                ? Encode(bitmap, imageLongEdge, bounds, maxImageLongEdge)
                : EncodeRegion(bitmap, bounds, crop, maxImageLongEdge);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Crops <paramref name="crop"/> out of a window bitmap and encodes it at
    /// native scale (only the protocol maximum can force a downscale). The
    /// payload's <c>sourceBounds</c> is the crop's physical rectangle.
    /// </summary>
    internal static ImagePayload? EncodeRegion(
        Bitmap bitmap,
        Rect sourceBounds,
        Rect crop,
        int maxImageLongEdge = SnapshotOptions.DefaultMaxImageLongEdge)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(sourceBounds);
        ArgumentNullException.ThrowIfNull(crop);

        var local = new Rectangle(
            crop.X - sourceBounds.X,
            crop.Y - sourceBounds.Y,
            crop.Width,
            crop.Height);
        if (local.X < 0
            || local.Y < 0
            || local.Width <= 0
            || local.Height <= 0
            || local.X + local.Width > bitmap.Width
            || local.Y + local.Height > bitmap.Height)
        {
            return null;
        }

        using var cropped = bitmap.Clone(local, bitmap.PixelFormat);
        return Encode(
            cropped,
            Math.Max(crop.Width, crop.Height),
            crop,
            maxImageLongEdge);
    }

    /// <summary>
    /// Downscales so the long edge is at most <paramref name="imageLongEdge"/>
    /// (clamped to <paramref name="maxImageLongEdge"/>, itself clamped to the
    /// protocol maximum), encodes PNG, and reports <c>scale</c> as physical
    /// pixels per returned image pixel.
    /// </summary>
    public static ImagePayload Encode(
        Bitmap bitmap,
        int imageLongEdge,
        Rect sourceBounds,
        int maxImageLongEdge = SnapshotOptions.DefaultMaxImageLongEdge)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(sourceBounds);

        var longEdge = ClampLongEdge(imageLongEdge, maxImageLongEdge);
        var longest = Math.Max(bitmap.Width, bitmap.Height);
        var factor = longest > longEdge ? longEdge / (double)longest : 1.0;

        Bitmap? rasterized = null;
        try
        {
            var image = bitmap;
            if (factor < 1.0)
            {
                var width = Math.Max(
                    1,
                    (int)Math.Round(bitmap.Width * factor, MidpointRounding.AwayFromZero));
                var height = Math.Max(
                    1,
                    (int)Math.Round(bitmap.Height * factor, MidpointRounding.AwayFromZero));
                rasterized = RasterizeOpaque(bitmap, width, height);
                image = rasterized;
            }
            else if (Image.IsAlphaPixelFormat(bitmap.PixelFormat))
            {
                // Saving an alpha-format bitmap whose alpha is zero yields a
                // fully transparent PNG no matter how much RGB it holds.
                rasterized = RasterizeOpaque(bitmap, bitmap.Width, bitmap.Height);
                image = rasterized;
            }

            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);

            return new ImagePayload
            {
                Data = Convert.ToBase64String(stream.ToArray()),
                MimeType = "image/png",
                Scale = sourceBounds.Width / (double)image.Width,
                SourceBounds = new Rect(
                    sourceBounds.X,
                    sourceBounds.Y,
                    sourceBounds.Width,
                    sourceBounds.Height),
            };
        }
        finally
        {
            rasterized?.Dispose();
        }
    }

    /// <summary>
    /// Draws the source onto an opaque surface and forces every output pixel to
    /// full alpha, so capture content stored with a zero alpha channel survives
    /// downscaling and PNG encoding.
    /// </summary>
    private static Bitmap RasterizeOpaque(Bitmap source, int width, int height)
    {
        var opaque = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(opaque);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(OpaqueAlphaMatrix);
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, width, height),
                0,
                0,
                source.Width,
                source.Height,
                GraphicsUnit.Pixel,
                attributes);
            return opaque;
        }
        catch (Exception)
        {
            opaque.Dispose();
            throw;
        }
    }

    private static int ClampLongEdge(int imageLongEdge, int maxImageLongEdge)
    {
        var maximum = Math.Clamp(
            maxImageLongEdge <= 0
                ? SnapshotOptions.DefaultMaxImageLongEdge
                : maxImageLongEdge,
            1,
            SnapshotOptions.DefaultMaxImageLongEdge);

        return Math.Clamp(
            imageLongEdge <= 0 ? SnapshotOptions.DefaultImageLongEdge : imageLongEdge,
            1,
            maximum);
    }

    private static Bitmap? TryPrintWindow(nint hwnd, Rect bounds)
    {
        // Capture surfaces are opaque RGB: GDI leaves the alpha byte of an ARGB
        // bitmap at zero, and that zero alpha would blank the image on encode.
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            bool printed;
            try
            {
                printed = PrintWindow(hwnd, hdc, PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            if (printed && !IsAllBlack(bitmap))
            {
                return bitmap;
            }
        }
        catch (Exception)
        {
            // Fall through to the on-screen fallback; Capture returns null when
            // that also fails.
        }

        bitmap.Dispose();
        return null;
    }

    private static Bitmap? TryCopyFromScreen(nint hwnd, Rect bounds)
    {
        // PrintWindow renders black for some windows while they are minimized;
        // restore them so the on-screen copy has content to grab.
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
            Thread.Sleep(RestoreSettleMilliseconds);
        }

        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                bounds.X,
                bounds.Y,
                0,
                0,
                new Size(bounds.Width, bounds.Height),
                CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch (Exception)
        {
            bitmap.Dispose();
            return null;
        }
    }

    /// <summary>
    /// True when every pixel is black, which is how some windows report a
    /// failed <c>PrintWindow</c> render.
    /// </summary>
    private static bool IsAllBlack(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
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

                    // Ignore the alpha byte: GDI-rendered bitmaps may leave it zero.
                    if (bytes[offset] != 0 || bytes[offset + 1] != 0 || bytes[offset + 2] != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int nCmdShow);
}
