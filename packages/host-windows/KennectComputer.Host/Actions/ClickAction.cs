using System.Runtime.ExceptionServices;

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
/// center. A non-left button, a count other than one, or any modifier makes
/// the click a mouse gesture, so semantic patterns are skipped and the
/// bounds-center (or coordinate) path runs instead. Coordinate targets map
/// through the snapshot's scale and source bounds (image space) or are already
/// physical (screen space). Modifiers are held around the clicks and released
/// in reverse on every path, and each click checks the stop token. Every
/// failure sends no input and is raised as an <see cref="ActionFailedException"/>.
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

        // An unknown modifier must fail before focus or any input.
        var modifiers = ParseModifiers(request.Modifiers);

        // The window identity is verified above; now check the caller's
        // preconditions against one fresh read, before any focus or input.
        await _preflight
            .CheckExpectationsAsync(resolved, request.Expect, cancellationToken)
            .ConfigureAwait(false);

        return hasNode
            ? await ClickNodeAsync(resolved, request, modifiers, cancellationToken)
                .ConfigureAwait(false)
            : await ClickPointAsync(resolved, request, modifiers, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<ActionResult> ClickNodeAsync(
        ResolvedWindow window,
        ClickRequest request,
        IReadOnlyList<ushort> modifiers,
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

        // A non-left button, a multi-click, or a modified click is a mouse
        // gesture: invoking a pattern could not express it.
        if (RequiresMouseGesture(request) is false
            && TrySemanticAction(live, cancellationToken))
        {
            return Succeeded(window.Info, "pattern");
        }

        // No pattern was available; click the element's current physical
        // bounds center instead of the bounds captured in the snapshot. The
        // center must be a real on-screen point inside the selected window.
        var center = CoordinateTarget.ResolveNodeCenter(live, window.Info, "click");
        cancellationToken.ThrowIfCancellationRequested();
        _input.MoveTo(center.X, center.Y);
        SendClick(request, modifiers, cancellationToken);

        return Succeeded(window.Info, "bounds");
    }

    private async Task<ActionResult> ClickPointAsync(
        ResolvedWindow window,
        ClickRequest request,
        IReadOnlyList<ushort> modifiers,
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

        // With the window foreground, a point that landed near a target can
        // be corrected to the live element under it. A container or a point
        // outside the window keeps the original coordinate.
        SnapInfo? snappedTo = null;
        var snap = _resolver.SnapToPoint(window.Hwnd, point.X, point.Y, cancellationToken);
        if (snap is not null
            && CoordinateTarget.TryBoundsCenter(snap.Bounds, window.Info, out var snappedPoint))
        {
            point = snappedPoint;
            snappedTo = new SnapInfo(snap.Role, snap.Name);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _input.MoveTo(point.X, point.Y);
        SendClick(request, modifiers, cancellationToken);

        return Succeeded(window.Info, "coordinates", snappedTo);
    }

    private static bool RequiresMouseGesture(ClickRequest request)
        => request.Button != MouseButton.Left
            || request.Count != 1
            || request.Modifiers.Count > 0;

    /// <summary>
    /// Parses the click's modifier names into virtual keys, rejecting an
    /// unknown name before the window is focused.
    /// </summary>
    private static IReadOnlyList<ushort> ParseModifiers(IReadOnlyList<string> modifiers)
    {
        if (modifiers.Count == 0)
        {
            return [];
        }

        var keys = new List<ushort>(modifiers.Count);
        foreach (var modifier in modifiers)
        {
            if (!KeyParser.TryParseModifier(modifier, out var virtualKey))
            {
                throw Failure(
                    ErrorCode.InvalidRequest,
                    $"The click modifier \"{modifier}\" is not Ctrl, Shift, Alt, or Win.");
            }

            keys.Add(virtualKey);
        }

        return keys;
    }

    /// <summary>
    /// Holds every modifier around the requested clicks and releases them in
    /// reverse. The stop token is checked before each modifier and each click,
    /// so a stop aborts a multi-click after at most one click; a failure after
    /// a modifier went down still releases every pressed modifier.
    /// </summary>
    private void SendClick(
        ClickRequest request,
        IReadOnlyList<ushort> modifiers,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        var pressed = new List<ushort>(modifiers.Count);

        try
        {
            foreach (var modifier in modifiers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _input.KeyDown(modifier);
                pressed.Add(modifier);
            }

            for (var index = 0; index < request.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _input.Click(request.Button, 1);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Exception? releaseFailure = null;
        for (var index = pressed.Count - 1; index >= 0; index--)
        {
            try
            {
                _input.KeyUp(pressed[index]);
            }
            catch (Exception exception)
            {
                releaseFailure ??= exception;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (releaseFailure is not null)
        {
            ExceptionDispatchInfo.Capture(releaseFailure).Throw();
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

    private static ActionResult Succeeded(
        WindowInfo window,
        string method,
        SnapInfo? snappedTo = null) => new()
    {
        Window = window,
        Action = "click",
        Method = method,
        NeedsObservation = true,
        SnappedTo = snappedTo,
    };

    private static ActionFailedException Failure(ErrorCode code, string message)
        => new(code, message);
}
