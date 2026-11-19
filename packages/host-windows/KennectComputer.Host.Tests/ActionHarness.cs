using KennectComputer.Host.Actions;
using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// Every M2 service wired to the actual Windows implementations, exactly as the
/// dispatcher wires them, with an owned window registered through a test
/// <see cref="IWindowApi"/> that re-reads the live form.
///
/// The harness is shared by the M2 input integration and the M3 flow dry run.
/// It takes a native handle, a live-bounds reader, and the registered native
/// identity, so each test window type supplies its own lifecycle without the
/// harness owning it.
/// </summary>
internal sealed class ActionHarness
{
    private readonly nint _handle;
    private readonly Func<Rect> _readBounds;

    public ActionHarness(nint handle, Func<Rect> readBounds, RawWindow rawWindow)
    {
        _handle = handle;
        _readBounds = readBounds ?? throw new ArgumentNullException(nameof(readBounds));
        ArgumentNullException.ThrowIfNull(rawWindow);

        // The registry sees only the owned window, but through the same live
        // identity contract the real WindowApi implements.
        Windows = new OwnedWindowApi(handle, readBounds, rawWindow);
        Registry = new WindowRegistry(Windows);
        SelectedWindow = Registry.Register(rawWindow);
        Selection = new SelectionStore();
        Selection.Select(SelectedWindow);

        Snapshots = new SnapshotStore();
        Walker = new WindowsUiaWalker();
        Resolver = new WindowsUiaNodeResolver();
        Clipboard = new WindowsClipboard();
        Input = new WindowsInputSender(new SendInputApi());
        Snapshotter = new UiaSnapshotter(Walker);

        var foregroundApi = new WindowsForegroundApi();
        var preflight = new ActionPreflight(
            new ForegroundService(foregroundApi),
            Selection,
            Registry,
            new ExpectEvaluator(Walker, foregroundApi),
            TimeSpan.FromSeconds(2));

        Click = new ClickAction(Walker, Resolver, preflight, Input, Snapshots);
        Type = new TypeAction(Resolver, preflight, Input, Clipboard, Snapshots);
        Key = new KeyAction(preflight, Input);
        Scroll = new ScrollAction(Resolver, preflight, Input, Snapshots);
        ReadText = new ReadTextAction(Resolver, Snapshots, Selection, Registry);
        Find = new FindAction(Walker, Snapshotter, Snapshots, Selection, Registry);
        Wait = new WaitAction(
            Walker,
            new WindowEnumerator(Windows, Registry),
            Selection,
            Registry,
            SystemWaitClock.Instance);
        Batch = new BatchAction(
            Click,
            Type,
            Key,
            Scroll,
            Wait,
            ReadText,
            Snapshotter,
            Snapshots,
            Selection,
            Registry);
    }

    public IWindowApi Windows { get; }

    public WindowRegistry Registry { get; }

    public WindowInfo SelectedWindow { get; }

    public SelectionStore Selection { get; }

    public SnapshotStore Snapshots { get; }

    public WindowsUiaWalker Walker { get; }

    public WindowsUiaNodeResolver Resolver { get; }

    public WindowsClipboard Clipboard { get; }

    public WindowsInputSender Input { get; }

    public UiaSnapshotter Snapshotter { get; }

    public ClickAction Click { get; }

    public TypeAction Type { get; }

    public KeyAction Key { get; }

    public ScrollAction Scroll { get; }

    public ReadTextAction ReadText { get; }

    public FindAction Find { get; }

    public WaitAction Wait { get; }

    public BatchAction Batch { get; }

    /// <summary>
    /// Takes a real accessibility (and optionally image) snapshot through the
    /// selected window and saves it for later node actions.
    /// </summary>
    public Snapshot Snapshot(bool image)
    {
        // One bounds read for the capture, the saved state, and later
        // coordinate mapping, so the three can never disagree.
        var bounds = _readBounds();
        var capture = Snapshotter.Capture(
            _handle,
            bounds,
            new SnapshotOptions
            {
                Mode = SnapshotMode.A11y,
                Image = image,
            },
            CancellationToken.None);

        capture.Snapshot.Window = SelectedWindow;
        Snapshots.Save(
            SelectedWindow.WindowId,
            capture.Snapshot,
            capture.NodeMap,
            bounds);

        Assert.Null(capture.Snapshot.Warning);
        return capture.Snapshot;
    }
}

/// <summary>
/// The registry's window source: only the owned test window, with its live
/// native identity. It re-reads the bounds, so a disposed window stops
/// resolving instead of acting on a dead handle.
/// </summary>
internal sealed class OwnedWindowApi : IWindowApi
{
    private readonly nint _handle;
    private readonly Func<Rect> _readBounds;
    private readonly RawWindow _rawWindow;

    public OwnedWindowApi(nint handle, Func<Rect> readBounds, RawWindow rawWindow)
    {
        _handle = handle;
        _readBounds = readBounds ?? throw new ArgumentNullException(nameof(readBounds));
        _rawWindow = rawWindow ?? throw new ArgumentNullException(nameof(rawWindow));
    }

    public IReadOnlyList<RawWindow> GetTopLevelWindows()
        => OwnedWindowNative.IsVisible(_handle) ? [Current()] : [];

    public RawWindow? TryGetWindow(nint hwnd)
        => hwnd == _handle && OwnedWindowNative.IsVisible(_handle) ? Current() : null;

    private RawWindow Current() => _rawWindow with { Bounds = _readBounds() };
}
