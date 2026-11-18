using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Actions;

/// <summary>
/// Turns the wheel in the selected window (DESIGN §6.2, §6.6).
///
/// The selected window is re-resolved first, so a dead or recycled window is
/// never scrolled. A node target must belong to the stored snapshot generation
/// and still match the live element's fingerprint; the wheel is aimed at the
/// live element's bounds center, not the captured bounds. Coordinate targets
/// map through the snapshot's scale and source bounds (image space) or are
/// already physical (screen space). Expectations run against a fresh
/// accessibility read and the window is verified foreground before the pointer
/// moves or the wheel turns. Direction and amount become a wheel delta of 120
/// per notch (clamped to 1..100 notches), negative for down and left, with the
/// horizontal wheel for left and right. Every failure sends no input and is
/// raised as an <see cref="ActionFailedException"/>.
/// </summary>
public sealed class ScrollAction
{
    private const int WheelNotch = 120;
    private const int MinAmount = 1;
    private const int MaxAmount = 100;

    private readonly IUiaNodeResolver _resolver;
    private readonly ActionPreflight _preflight;
    private readonly IInputSender _input;
    private readonly SnapshotStore _snapshots;

    public ScrollAction(
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
        ScrollRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = _preflight.ResolveSelectedWindow("scrolling");

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

        // The delta is pure request validation; computing it here keeps an
        // unknown direction from walking the tree or reaching input.
        var (delta, horizontal) = WheelDelta(request);

        // The window identity is verified above; now check the caller's
        // preconditions against one fresh read, before any focus or input.
        await _preflight
            .CheckExpectationsAsync(resolved, request.Expect, cancellationToken)
            .ConfigureAwait(false);

        return hasNode
            ? await ScrollNodeAsync(resolved, request, delta, horizontal, cancellationToken)
                .ConfigureAwait(false)
            : await ScrollPointAsync(resolved, request, delta, horizontal, cancellationToken)
                .ConfigureAwait(false);
    }

    private static (int Delta, bool Horizontal) WheelDelta(ScrollRequest request)
    {
        var notches = Math.Clamp(request.Amount, MinAmount, MaxAmount);

        return request.Direction switch
        {
            ScrollDirection.Up => (notches * WheelNotch, false),
            ScrollDirection.Down => (-notches * WheelNotch, false),
            ScrollDirection.Left => (-notches * WheelNotch, true),
            ScrollDirection.Right => (notches * WheelNotch, true),
            _ => throw Failure(
                ErrorCode.InvalidRequest,
                "The scroll direction is invalid."),
        };
    }

    private async Task<ActionResult> ScrollNodeAsync(
        ResolvedWindow window,
        ScrollRequest request,
        int delta,
        bool horizontal,
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
                "The node is from an older snapshot; take a new snapshot before scrolling.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var live = _resolver.Resolve(window.Hwnd, target, cancellationToken)
            ?? throw Failure(
                ErrorCode.StaleSnapshot,
                "The node no longer matches the live element; take a new snapshot.");

        await _preflight
            .FocusAsync(
                window.Hwnd,
                "Windows refused to bring the window to the foreground; no scroll was sent.",
                cancellationToken)
            .ConfigureAwait(false);

        // Aim at where the element is now, not where the snapshot captured it,
        // and only at a real on-screen point inside the selected window.
        var center = CoordinateTarget.ResolveNodeCenter(live, window.Info, "scroll");
        cancellationToken.ThrowIfCancellationRequested();
        _input.MoveTo(center.X, center.Y);
        cancellationToken.ThrowIfCancellationRequested();
        _input.Scroll(delta, horizontal);

        return Succeeded(window.Info);
    }

    private async Task<ActionResult> ScrollPointAsync(
        ResolvedWindow window,
        ScrollRequest request,
        int delta,
        bool horizontal,
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
            "scroll");

        await _preflight
            .FocusAsync(
                window.Hwnd,
                "Windows refused to bring the window to the foreground; no scroll was sent.",
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        _input.MoveTo(point.X, point.Y);
        cancellationToken.ThrowIfCancellationRequested();
        _input.Scroll(delta, horizontal);

        return Succeeded(window.Info);
    }

    private static ActionResult Succeeded(WindowInfo window) => new()
    {
        Window = window,
        Action = "scroll",
        Method = "wheel",
        NeedsObservation = true,
    };

    private static ActionFailedException Failure(ErrorCode code, string message)
        => new(code, message);
}
