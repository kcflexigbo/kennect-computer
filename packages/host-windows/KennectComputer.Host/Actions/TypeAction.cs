using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;

namespace KennectComputer.Host.Actions;

/// <summary>
/// Types text into the selected window (DESIGN §6.4).
///
/// The selected window is re-resolved first, so a dead or recycled window is
/// never typed into. Expectations run against a fresh accessibility read before
/// any focus change or input. A node target must belong to the stored snapshot
/// generation and still match the live element's fingerprint; the live element
/// is then focused before text reaches it. The window itself must be verified
/// foreground before any input, so a focus refusal sends nothing to another
/// app.
///
/// Paste (the default) saves the clipboard, sets the text, sends Ctrl+V, and
/// restores the saved text in a <c>finally</c> even when the chord throws. The
/// chord releases Ctrl in its own <c>finally</c>, so a failed or partially
/// delivered key event cannot leave a stuck modifier. If the clipboard cannot
/// be read there is no way to put the user's content back, so the action fails
/// with <c>INVALID_REQUEST</c> before writing anything. Value sets the
/// accessibility value where supported and falls back to paste. Keys sends one
/// Unicode event per UTF-16 code unit and never touches the clipboard. Action
/// failures are raised as <see cref="ActionFailedException"/>; cancellation and
/// input-send failures propagate unchanged.
/// </summary>
public sealed class TypeAction
{
    private const ushort VkControl = 0x11;
    private const ushort VkPaste = 0x56;

    private readonly IUiaNodeResolver _resolver;
    private readonly ActionPreflight _preflight;
    private readonly IInputSender _input;
    private readonly IClipboard _clipboard;
    private readonly SnapshotStore _snapshots;

    public TypeAction(
        IUiaNodeResolver resolver,
        ActionPreflight preflight,
        IInputSender input,
        IClipboard clipboard,
        SnapshotStore snapshots)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    }

    public async Task<ActionResult> ExecuteAsync(
        TypeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // The shared schema already rejects empty text; the host repeats the
        // check so a malformed request can never reach the clipboard or input.
        if (string.IsNullOrEmpty(request.Text))
        {
            throw Failure(ErrorCode.InvalidRequest, "Type text must not be empty.");
        }

        // Value needs a live element to set. Without one the request is
        // malformed rather than a paste, which could target the wrong control.
        if (request.Method == TypeMethod.Value
            && string.IsNullOrWhiteSpace(request.NodeId))
        {
            throw Failure(
                ErrorCode.InvalidRequest,
                "method \"value\" requires a nodeId to set the value on.");
        }

        var resolved = _preflight.ResolveSelectedWindow("typing");

        // The window identity is verified above; now check the caller's
        // preconditions against one fresh read, before any focus or input.
        await _preflight
            .CheckExpectationsAsync(resolved, request.Expect, cancellationToken)
            .ConfigureAwait(false);

        IUiaNode? live = null;
        if (!string.IsNullOrWhiteSpace(request.NodeId))
        {
            // TryGetNode re-checks the node ID's generation against the stored
            // snapshot, so an ID from an older generation never reaches the
            // resolver or the focused control.
            if (!_snapshots.TryGetNode(resolved.Info.WindowId, request.NodeId, out var target)
                || target is null)
            {
                throw Failure(
                    ErrorCode.StaleSnapshot,
                    "The node is from an older snapshot; take a new snapshot before typing.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            live = _resolver.Resolve(resolved.Hwnd, target, cancellationToken)
                ?? throw Failure(
                    ErrorCode.StaleSnapshot,
                    "The node no longer matches the live element; take a new snapshot.");
        }

        // Input only ever reaches a window the preflight verified foreground.
        // A node target then focuses the live element itself, so text cannot
        // land in whichever control happened to be focused.
        await _preflight
            .FocusAsync(
                resolved.Hwnd,
                "Windows refused to bring the window to the foreground; no text was typed.",
                cancellationToken)
            .ConfigureAwait(false);

        if (live is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!live.TryFocus())
            {
                throw Failure(
                    ErrorCode.FocusRefused,
                    "The control refused focus; no text was typed.");
            }
        }

        var method = ExecuteMethod(live, request, cancellationToken);
        return Succeeded(resolved.Info, method);
    }

    /// <summary>
    /// Carries out the requested method and reports the method that actually
    /// ran: an unsupported value pattern falls back to paste.
    /// </summary>
    private TypeMethod ExecuteMethod(
        IUiaNode? live,
        TypeRequest request,
        CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case TypeMethod.Value:
                cancellationToken.ThrowIfCancellationRequested();

                // The request validation above guarantees a node, so this is
                // defence in depth rather than a reachable fallback.
                if (live is not null && live.TrySetValue(request.Text))
                {
                    return TypeMethod.Value;
                }

                Paste(request.Text, cancellationToken);
                return TypeMethod.Paste;

            case TypeMethod.Keys:
                TypeKeys(request.Text, cancellationToken);
                return TypeMethod.Keys;

            default:
                Paste(request.Text, cancellationToken);
                return TypeMethod.Paste;
        }
    }

    /// <summary>
    /// Saves the clipboard, sets the text, sends Ctrl+V, and restores the saved
    /// text no matter how the send ends. The save is the only step that may not
    /// fail silently: without it the action cannot guarantee the typed text is
    /// removed, so it fails before writing anything.
    /// </summary>
    private void Paste(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? saved;
        try
        {
            saved = _clipboard.GetText();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failure(
                ErrorCode.InvalidRequest,
                "The clipboard could not be read; no text was typed.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _clipboard.SetText(text);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Failure(
                    ErrorCode.InvalidRequest,
                    "The clipboard could not be updated; no text was typed.");
            }

            // Ctrl is held only for the chord, and it is released in finally:
            // cancellation or a failed/partially delivered key event between
            // the presses must not leave a system-wide stuck modifier. The
            // token is checked once before the chord, never between its
            // events, because an abort there could strand Ctrl down.
            cancellationToken.ThrowIfCancellationRequested();
            _input.KeyDown(VkControl);
            try
            {
                _input.KeyDown(VkPaste);
                _input.KeyUp(VkPaste);
            }
            finally
            {
                _input.KeyUp(VkControl);
            }
        }
        finally
        {
            // Deliberately not cancellable: the typed text must not outlive the
            // action, so the restore runs even when the token was signalled
            // partway through the chord.
            RestoreClipboard(saved);
        }
    }

    /// <summary>
    /// Types one Unicode event per UTF-16 code unit. The input sender treats
    /// each unit independently, so a surrogate pair travels as two events and
    /// CJK text does not depend on the active keyboard layout.
    /// </summary>
    private void TypeKeys(string text, CancellationToken cancellationToken)
    {
        foreach (var character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _input.UnicodeText(character.ToString());
        }
    }

    /// <summary>
    /// Best-effort restore per DESIGN §6.2. A failure cannot rewrite the
    /// original outcome, but it must not leave the typed text behind either, so
    /// when the user's text cannot be put back the clipboard is cleared once.
    /// </summary>
    private void RestoreClipboard(string? saved)
    {
        var restore = saved ?? string.Empty;

        try
        {
            _clipboard.SetText(restore);
        }
        catch (Exception)
        {
            if (restore.Length == 0)
            {
                return;
            }

            try
            {
                _clipboard.SetText(string.Empty);
            }
            catch (Exception)
            {
                // The clipboard is still held by another app; nothing more can
                // be done without masking the action's real outcome.
            }
        }
    }

    private static ActionResult Succeeded(WindowInfo window, TypeMethod method) => new()
    {
        Window = window,
        Action = "type",
        Method = MethodName(method),
        NeedsObservation = true,
    };

    private static string MethodName(TypeMethod method) => method switch
    {
        TypeMethod.Value => "value",
        TypeMethod.Keys => "keys",
        _ => "paste",
    };

    private static ActionFailedException Failure(ErrorCode code, string message)
        => new(code, message);
}
