using KennectComputer.Host.Capture;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Uia;

/// <summary>
/// The platform seam for reading an accessibility tree. Tests substitute a
/// deterministic walker; the host uses <see cref="WindowsUiaWalker"/>.
/// </summary>
public interface IUiaWalker
{
    TreeModel? Walk(nint hwnd, SnapshotOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// The platform seam for capturing the optional snapshot image. Tests
/// substitute a deterministic delegate; the host uses <see cref="ScreenCapture"/>.
/// Returns null when the image is not requested or capture fails.
/// </summary>
public delegate ImagePayload? WindowCapture(
    nint hwnd,
    Rect bounds,
    int imageLongEdge,
    bool enabled);

/// <summary>A snapshot plus the node map needed to act on its node IDs later.</summary>
public sealed record SnapshotCapture(
    Snapshot Snapshot,
    IReadOnlyDictionary<string, TreeModel> NodeMap);

/// <summary>
/// Produces the compact accessibility part of a snapshot.
///
/// Chromium enables its accessibility tree lazily, so an empty first walk is
/// normal (R9). The snapshotter focuses the window and re-walks up to three
/// times with a 1.5 s wait between attempts; if the tree stays empty it returns
/// the empty outline with the vision-fallback warning instead of hanging. Every
/// call increments the generation, and node IDs embed it.
///
/// Focus and wait are constructor seams so unit tests never touch the desktop
/// or sleep.
/// </summary>
public sealed class UiaSnapshotter
{
    public const string EmptyTreeWarning = "accessibility tree empty; vision fallback required";
    public const int MaxWarmupRetries = 3;

    private static readonly TimeSpan WarmupDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly IReadOnlyDictionary<string, TreeModel> EmptyNodeMap =
        new Dictionary<string, TreeModel>(StringComparer.Ordinal);
    private static readonly IForegroundApi FocusApi = new WindowsForegroundApi();

    private readonly IUiaWalker _walker;
    private readonly Action<nint> _focusWindow;
    private readonly Action<CancellationToken> _wait;
    private readonly WindowCapture _captureImage;
    private int _generation;

    public UiaSnapshotter(
        IUiaWalker walker,
        Action<nint>? focusWindow = null,
        Action<CancellationToken>? wait = null,
        WindowCapture? capture = null)
    {
        _walker = walker ?? throw new ArgumentNullException(nameof(walker));
        _focusWindow = focusWindow ?? (hwnd => FocusApi.SetForegroundWindow(hwnd));
        _wait = wait ?? (cancellationToken =>
        {
            cancellationToken.WaitHandle.WaitOne(WarmupDelay);
            cancellationToken.ThrowIfCancellationRequested();
        });
        _captureImage = capture ?? ScreenCapture.Capture;
    }

    public Snapshot Snapshot(
        nint hwnd,
        Rect bounds,
        SnapshotOptions options,
        CancellationToken cancellationToken)
        => Capture(hwnd, bounds, options, cancellationToken).Snapshot;

    public SnapshotCapture Capture(
        nint hwnd,
        Rect bounds,
        SnapshotOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var generation = Interlocked.Increment(ref _generation);

        if (options.Mode == SnapshotMode.Vision)
        {
            // Vision-only callers explicitly asked to skip structure, but the
            // image is the whole point of that mode, so capture it anyway.
            return new SnapshotCapture(
                new Snapshot
                {
                    Generation = generation,
                    Nodes = [],
                    Image = CaptureImage(hwnd, bounds, options),
                },
                EmptyNodeMap);
        }

        TreeModel? tree = null;
        SliceResult? slice = null;

        for (var attempt = 0; attempt <= MaxWarmupRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                _focusWindow(hwnd);
                _wait(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            tree = _walker.Walk(hwnd, options, cancellationToken);
            if (tree is null)
            {
                continue;
            }

            slice = TreeSlicer.Slice(tree, options, generation);
            if (slice.Nodes.Count > 0)
            {
                break;
            }
        }

        var image = CaptureImage(hwnd, bounds, options);

        if (tree is null || slice is null || slice.Nodes.Count == 0)
        {
            // The empty tree is the vision-fallback case, so the image the
            // caller asked for still accompanies the warning.
            return new SnapshotCapture(
                new Snapshot
                {
                    Generation = generation,
                    Modal = tree is not null && AnyVisibleModal(tree),
                    Nodes = [],
                    TotalNodes = slice?.TotalNodes ?? 0,
                    Truncated = slice?.Truncated ?? false,
                    Image = image,
                    Warning = EmptyTreeWarning,
                },
                EmptyNodeMap);
        }

        return new SnapshotCapture(
            new Snapshot
            {
                Generation = generation,
                FocusedNodeId = slice.Nodes.FirstOrDefault(node => node.Focused)?.NodeId,
                Modal = AnyVisibleModal(tree),
                Nodes = [.. slice.Nodes],
                TotalNodes = slice.TotalNodes,
                Truncated = slice.Truncated,
                Image = image,
            },
            slice.NodeMap);
    }

    /// <summary>
    /// Captures the optional image. <see cref="ScreenCapture.Capture"/> never
    /// throws and returns null when capture fails, so the accessibility
    /// snapshot and its warning are unaffected.
    /// </summary>
    private ImagePayload? CaptureImage(nint hwnd, Rect bounds, SnapshotOptions options)
        => options.Image ? _captureImage(hwnd, bounds, options.ImageLongEdge, enabled: true) : null;

    private static bool AnyVisibleModal(TreeModel node)
        => (!node.Offscreen && node.IsModal) || node.Children.Any(AnyVisibleModal);
}
