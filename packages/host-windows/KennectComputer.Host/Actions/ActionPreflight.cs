using KennectComputer.Host.Control;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Actions;

/// <summary>
/// The guards every action runs before it touches the desktop, kept in one
/// place so the ordering cannot drift between actions (R13).
///
/// Callers resolve the selected window, validate their own target, check the
/// caller's expectations against one fresh accessibility read, and only then
/// bring the window to the foreground. Resolution rejects a dead or recycled
/// window and clears a selection that no longer resolves; expectation failures
/// carry the first failing predicate; focus refusal is reported as
/// <c>FOCUS_REFUSED</c>. None of these paths sends input.
/// </summary>
public sealed class ActionPreflight
{
    private static readonly TimeSpan DefaultFocusTimeout = TimeSpan.FromSeconds(1);

    private readonly ForegroundService _foreground;
    private readonly SelectionStore _selection;
    private readonly WindowRegistry _registry;
    private readonly ExpectEvaluator _expect;
    private readonly TimeSpan _focusTimeout;

    public ActionPreflight(
        ForegroundService foreground,
        SelectionStore selection,
        WindowRegistry registry,
        ExpectEvaluator expect,
        TimeSpan? focusTimeout = null)
    {
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _expect = expect ?? throw new ArgumentNullException(nameof(expect));
        _focusTimeout = focusTimeout ?? DefaultFocusTimeout;
    }

    /// <summary>
    /// Resolves the selected window to its live native identity. A missing
    /// selection is <c>INVALID_REQUEST</c>; a window that no longer resolves is
    /// <c>WINDOW_GONE</c> and the selection is cleared, so a dead ID can never
    /// drift to another window. <paramref name="pendingAction"/> completes the
    /// caller-facing "select a window before …" message.
    /// </summary>
    public ResolvedWindow ResolveSelectedWindow(string pendingAction)
    {
        ArgumentException.ThrowIfNullOrEmpty(pendingAction);

        var selected = _selection.Selected
            ?? throw Failure(
                ErrorCode.InvalidRequest,
                $"No window is selected; select a window before {pendingAction}.");

        if (!_registry.TryResolve(selected.WindowId, out var resolved) || resolved is null)
        {
            _selection.Clear();
            throw Failure(ErrorCode.WindowGone, "The window is gone.");
        }

        return resolved;
    }

    /// <summary>
    /// Evaluates the caller's preconditions against one fresh accessibility
    /// read and throws <c>EXPECTATION_FAILED</c> naming the first failing
    /// predicate. Runs before any focus change or input.
    /// </summary>
    public async Task CheckExpectationsAsync(
        ResolvedWindow window,
        IReadOnlyList<ExpectPredicate> expect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(expect);
        cancellationToken.ThrowIfCancellationRequested();

        var expectation = await _expect
            .EvaluateAsync(window.Hwnd, expect, cancellationToken)
            .ConfigureAwait(false);

        if (!expectation.Ok)
        {
            throw new ActionFailedException(
                ErrorCode.ExpectationFailed,
                $"The expect predicate failed: {expectation.FailedPredicate}.",
                expectation.FailedPredicate);
        }
    }

    /// <summary>
    /// Brings the window to the foreground and verifies it. A refusal throws
    /// <see cref="ErrorCode.FocusRefused"/> with the caller's explanation;
    /// input is never sent to a window that was not verified foreground.
    /// </summary>
    public async Task FocusAsync(
        nint hwnd,
        string refusalMessage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(refusalMessage);
        cancellationToken.ThrowIfCancellationRequested();

        var focus = await _foreground.FocusAsync(hwnd, _focusTimeout).ConfigureAwait(false);
        if (!focus.Ok)
        {
            throw Failure(
                focus.Error ?? ErrorCode.FocusRefused,
                refusalMessage);
        }
    }

    private static ActionFailedException Failure(ErrorCode code, string message)
        => new(code, message);
}
