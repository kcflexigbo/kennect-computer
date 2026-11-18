using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Actions;

/// <summary>
/// Clicks a snapshot node or a point in the selected window (DESIGN §6.1–§6.3).
///
/// The selected window is re-resolved first, so a dead or recycled window is
/// never acted on. Expectations run against a fresh accessibility read before
/// any focus change or input. A node target must belong to the stored snapshot
/// generation and still match the live element's fingerprint; the action then
/// prefers a semantic UIA pattern and falls back to clicking the live bounds
/// center. Coordinate targets map through the snapshot's scale and source
/// bounds (image space) or are already physical (screen space). Every failure
/// sends no input and is raised as an <see cref="ActionFailedException"/>.
/// </summary>
public sealed class ClickAction
{
    private static readonly TimeSpan DefaultFocusTimeout = TimeSpan.FromSeconds(1);

    private readonly IUiaNodeResolver _resolver;
    private readonly ForegroundService _foreground;
    private readonly IInputSender _input;
    private readonly SnapshotStore _snapshots;
    private readonly SelectionStore _selection;
    private readonly WindowRegistry _registry;
    private readonly ExpectEvaluator _expect;
    private readonly TimeSpan _focusTimeout;

    public ClickAction(
        IUiaNodeResolver resolver,
        ForegroundService foreground,
        IInputSender input,
        SnapshotStore snapshots,
        SelectionStore selection,
        WindowRegistry registry,
        ExpectEvaluator expect,
        TimeSpan? focusTimeout = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _expect = expect ?? throw new ArgumentNullException(nameof(expect));
        _focusTimeout = focusTimeout ?? DefaultFocusTimeout;
    }

    public async Task<ActionResult> ExecuteAsync(
        ClickRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var selected = _selection.Selected
            ?? throw Failure(
                ErrorCode.InvalidRequest,
                "No window is selected; select a window before clicking.");

        if (!_registry.TryResolve(selected.WindowId, out var resolved) || resolved is null)
        {
            _selection.Clear();
            throw Failure(ErrorCode.WindowGone, "The window is gone.");
        }

        var hasNode = !string.IsNullOrWhiteSpace(request.NodeId);
        var hasX = request.X is not null;
        var hasY = request.Y is not null;

        // The shared schema allows exactly one target: a node ID, or both
        // coordinates. A mixed or partial target is rejected before anything
        // is evaluated so a malformed request can never reach input.
        if (hasNode ? hasX || hasY : !(hasX && hasY))
        {
            throw Failure(
                ErrorCode.InvalidRequest,
                "Provide exactly one of nodeId or x and y.");
        }

        if (request.Count < 1)
        {
            throw Failure(ErrorCode.InvalidRequest, "Click count must be at least 1.");
        }

        // The window identity is verified above; now check the caller's
        // preconditions against one fresh read, before any focus or input.
        cancellationToken.ThrowIfCancellationRequested();
        var expectation = await _expect
            .EvaluateAsync(resolved.Hwnd, request.Expect, cancellationToken)
            .ConfigureAwait(false);

        if (!expectation.Ok)
        {
            throw new ActionFailedException(
                ErrorCode.ExpectationFailed,
                $"The expect predicate failed: {expectation.FailedPredicate}.",
                expectation.FailedPredicate);
        }

        return hasNode
            ? await ClickNodeAsync(resolved, request, cancellationToken).ConfigureAwait(false)
            : await ClickPointAsync(resolved, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActionResult> ClickNodeAsync(
        ResolvedWindow window,
        ClickRequest request,
        CancellationToken cancellationToken)
    {
        // TryGetNode re-checks the node ID's generation against the stored
        // snapshot, so an ID from an older generation never reaches the
        // resolver or the screen.
        if (!_snapshots.TryGetNode(window.Info.WindowId, request.NodeId, out var target)
            || target is null)
        {
            throw Failure(
                ErrorCode.StaleSnapshot,
                "The node is from an older snapshot; take a new snapshot before clicking.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var live = _resolver.Resolve(window.Hwnd, target, cancellationToken)
            ?? throw Failure(
                ErrorCode.StaleSnapshot,
                "The node no longer matches the live element; take a new snapshot.");

        // Foreground is verified by FocusAsync before any pattern runs: a
        // semantic activation can still change another window's state.
        await FocusAsync(window.Hwnd, cancellationToken).ConfigureAwait(false);

        if (TrySemanticAction(live, cancellationToken))
        {
            return Succeeded(window.Info, "pattern");
        }

        // No pattern was available; click the element's current physical
        // bounds center instead of the bounds captured in the snapshot.
        var center = BoundsCenter(live.Bounds);
        cancellationToken.ThrowIfCancellationRequested();
        _input.MoveTo(center.X, center.Y);
        cancellationToken.ThrowIfCancellationRequested();
        _input.Click(request.Button, request.Count);

        return Succeeded(window.Info, "bounds");
    }

    private async Task<ActionResult> ClickPointAsync(
        ResolvedWindow window,
        ClickRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SnapshotState? state = _snapshots.TryGetState(window.Info.WindowId, out var stored)
            ? stored
            : null;

        if (request.Space == CoordinateSpace.Image && state is null)
        {
            throw Failure(
                ErrorCode.InvalidRequest,
                "An image-space click requires a snapshot with an image; "
                + "snapshot the window with image enabled.");
        }

        // Screen space is physical and independent of the snapshot; the
        // mapper's screen path reads only the current window, so an empty
        // state stands in when no snapshot has been taken.
        var mapping = CoordinateMapper.Map(
            state ?? ScreenOnlyState(window.Info),
            window.Info,
            request.X!.Value,
            request.Y!.Value,
            request.Space);

        if (mapping.Error is { } error)
        {
            throw Failure(error, CoordinateErrorMessage(error));
        }

        if (mapping.Point is not { } point)
        {
            throw Failure(ErrorCode.InvalidRequest, "The click coordinates could not be mapped.");
        }

        await FocusAsync(window.Hwnd, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        _input.MoveTo(point.X, point.Y);
        cancellationToken.ThrowIfCancellationRequested();
        _input.Click(request.Button, request.Count);

        return Succeeded(window.Info, "coordinates");
    }

    private async Task FocusAsync(nint hwnd, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var focus = await _foreground.FocusAsync(hwnd, _focusTimeout).ConfigureAwait(false);
        if (!focus.Ok)
        {
            throw Failure(
                focus.Error ?? ErrorCode.FocusRefused,
                "Windows refused to bring the window to the foreground; no input was sent.");
        }
    }

    /// <summary>
    /// Tries the activation patterns in preference order, stopping at the
    /// first one the live element supports. Cancellation is checked between
    /// attempts because each call crosses into the app's UIA provider.
    /// </summary>
    private static bool TrySemanticAction(IUiaNode node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node.TryInvoke())
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (node.TryToggle())
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (node.TrySelect())
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return node.TryExpand();
    }

    private static Input.Point BoundsCenter(Rect bounds) => new(
        bounds.X + (bounds.Width / 2),
        bounds.Y + (bounds.Height / 2));

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

    private static string CoordinateErrorMessage(ErrorCode error) => error switch
    {
        ErrorCode.StaleSnapshot =>
            "The window moved or resized since the snapshot; "
            + "take a new snapshot or use screen coordinates.",
        ErrorCode.InvalidRequest =>
            "The coordinates are outside the target window or the snapshot has no image.",
        _ => $"The click coordinates could not be mapped ({error.ToProtocolCode()}).",
    };

    private static ActionResult Succeeded(WindowInfo window, string method) => new()
    {
        Window = window,
        Action = "click",
        Method = method,
        NeedsObservation = true,
    };

    private static ActionFailedException Failure(ErrorCode code, string message)
        => new(code, message);
}
