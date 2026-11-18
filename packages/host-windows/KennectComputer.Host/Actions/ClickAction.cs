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
    private readonly IUiaNodeResolver _resolver;
    private readonly ActionPreflight _preflight;
    private readonly IInputSender _input;
    private readonly SnapshotStore _snapshots;

    public ClickAction(
        IUiaNodeResolver resolver,
        ActionPreflight preflight,
        IInputSender input,
        SnapshotStore snapshots)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    }

    public async Task<ActionResult> ExecuteAsync(
        ClickRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = _preflight.ResolveSelectedWindow("clicking");

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
        await _preflight
            .CheckExpectationsAsync(resolved, request.Expect, cancellationToken)
            .ConfigureAwait(false);

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

        // Foreground is verified before any pattern runs: a semantic
        // activation can still change another window's state.
        await _preflight
            .FocusAsync(
                window.Hwnd,
                "Windows refused to bring the window to the foreground; no input was sent.",
                cancellationToken)
            .ConfigureAwait(false);

        if (TrySemanticAction(live, cancellationToken))
        {
            return Succeeded(window.Info, "pattern");
        }

        // No pattern was available; click the element's current physical
        // bounds center instead of the bounds captured in the snapshot.
        var center = CoordinateTarget.BoundsCenter(live.Bounds);
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

        // The shared helper owns the image-space snapshot guard, the mapper
        // call, and the no-snapshot screen-only workaround.
        var point = CoordinateTarget.Resolve(
            state,
            window.Info,
            request.X!.Value,
            request.Y!.Value,
            request.Space,
            "click");

        await _preflight
            .FocusAsync(
                window.Hwnd,
                "Windows refused to bring the window to the foreground; no input was sent.",
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        _input.MoveTo(point.X, point.Y);
        cancellationToken.ThrowIfCancellationRequested();
        _input.Click(request.Button, request.Count);

        return Succeeded(window.Info, "coordinates");
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
