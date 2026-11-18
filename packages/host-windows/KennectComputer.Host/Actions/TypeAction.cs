using System.Runtime.InteropServices;

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
/// keeps the typed text on the clipboard until the target has consumed the
/// chord. A readable target is polled for the pasted text with a bounded,
/// cancellation-aware deadline; otherwise the target's input queue is drained,
/// also bounded. Only then is the saved text restored, in a <c>finally</c>, so
/// the target can never read the restored clipboard instead of the paste and
/// the typed text is never left behind. The chord releases Ctrl in its own
/// <c>finally</c>, so a failed or partially delivered key event cannot leave a
/// stuck modifier. If the clipboard cannot be read there is no way to put the
/// user's content back, so the action fails with <c>INVALID_REQUEST</c> before
/// writing anything. Value sets the accessibility value where supported and
/// falls back to paste. Keys sends one Unicode event per UTF-16 code unit and
/// never touches the clipboard. Action failures are raised as
/// <see cref="ActionFailedException"/>; cancellation and input-send failures
/// propagate unchanged.
/// </summary>
public sealed class TypeAction
{
    private const ushort VkControl = 0x11;
    private const ushort VkPaste = 0x56;
    private const uint WmNull = 0;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;

    /// <summary>
    /// How long a paste keeps the typed text on the clipboard while waiting for
    /// the target to consume it. The bound is what makes an unresponsive target
    /// safe: the user's clipboard is always restored within this window.
    /// </summary>
    public static readonly TimeSpan DefaultPasteConsumptionTimeout =
        TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PastePollInterval = TimeSpan.FromMilliseconds(25);

    private readonly IUiaNodeResolver _resolver;
    private readonly ActionPreflight _preflight;
    private readonly IInputSender _input;
    private readonly IClipboard _clipboard;
    private readonly SnapshotStore _snapshots;
    private readonly IWaitClock _clock;
    private readonly TimeSpan _pasteConsumptionTimeout;
    private readonly InputQueueDrain _drainInputQueue;

    public TypeAction(
        IUiaNodeResolver resolver,
        ActionPreflight preflight,
        IInputSender input,
        IClipboard clipboard,
        SnapshotStore snapshots,
        IWaitClock? clock = null,
        TimeSpan? pasteConsumptionTimeout = null,
        InputQueueDrain? drainInputQueue = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _clock = clock ?? SystemWaitClock.Instance;
        _pasteConsumptionTimeout = pasteConsumptionTimeout ?? DefaultPasteConsumptionTimeout;
        _drainInputQueue = drainInputQueue ?? DrainInputQueue;
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

        var method = await ExecuteMethodAsync(live, request, resolved.Hwnd, cancellationToken)
            .ConfigureAwait(false);
        return Succeeded(resolved.Info, method);
    }

    /// <summary>
    /// Carries out the requested method and reports the method that actually
    /// ran: an unsupported value pattern falls back to paste.
    /// </summary>
    private async Task<TypeMethod> ExecuteMethodAsync(
        IUiaNode? live,
        TypeRequest request,
        nint hwnd,
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

                await PasteAsync(live, hwnd, request.Text, cancellationToken)
                    .ConfigureAwait(false);
                return TypeMethod.Paste;

            case TypeMethod.Keys:
                TypeKeys(request.Text, cancellationToken);
                return TypeMethod.Keys;

            default:
                await PasteAsync(live, hwnd, request.Text, cancellationToken)
                    .ConfigureAwait(false);
                return TypeMethod.Paste;
        }
    }

    /// <summary>
    /// Saves the clipboard, sets the text, sends Ctrl+V, waits (bounded) for the
    /// target to consume the paste, and only then restores the saved text, no
    /// matter how the send or the wait ends. The save is the only step that may
    /// not fail silently: without it the action cannot guarantee the typed text
    /// is removed, so it fails before writing anything.
    /// </summary>
    private async Task PasteAsync(
        IUiaNode? live,
        nint hwnd,
        string text,
        CancellationToken cancellationToken)
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

        // Read the target's content just before the chord, so a later poll can
        // tell the pasted text apart from content that was already there.
        _ = TryReadContent(live, out var baseline);

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

            // The typed text must stay on the clipboard until the target has
            // read it. Restoring immediately races the target's Ctrl+V
            // processing, which can paste the user's previous clipboard.
            await WaitForPasteConsumptionAsync(live, hwnd, baseline, text, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Deliberately not cancellable: the typed text must not outlive the
            // action, so the restore runs even when the token was signalled
            // partway through the chord or the consumption wait.
            RestoreClipboard(saved);
        }
    }

    /// <summary>
    /// Waits, bounded, for the target to process the paste. A target whose
    /// content can be read is polled for the pasted text, which is the direct
    /// signal that its Ctrl+V ran. Otherwise the action drains the window's
    /// input queue, which is best effort but still bounded. Cancellation
    /// propagates; the caller restores the clipboard in a <c>finally</c>.
    /// </summary>
    private async Task WaitForPasteConsumptionAsync(
        IUiaNode? live,
        nint hwnd,
        string? baseline,
        string text,
        CancellationToken cancellationToken)
    {
        if (live is not null && baseline is not null)
        {
            var deadline = _clock.UtcNow + _pasteConsumptionTimeout;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (HasConsumedPaste(live, baseline, text))
                {
                    return;
                }

                if (_clock.UtcNow >= deadline)
                {
                    // The target is unobservable or slow; restoring is still
                    // bounded and the wait already gave it the full window.
                    return;
                }

                await _clock.Delay(PastePollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // No readable content: wait for the target's input queue to process the
        // chord instead. An app that defers the paste beyond the bound is not
        // observable; the clipboard is restored when the bound expires.
        cancellationToken.ThrowIfCancellationRequested();
        _drainInputQueue(hwnd, _pasteConsumptionTimeout, cancellationToken);
    }

    /// <summary>
    /// True once the target's content contains the pasted text and that cannot
    /// be explained by content that was already there. A paste that leaves the
    /// content identical (for example replacing a selection with the same
    /// text) cannot be distinguished and simply waits out the bound.
    /// </summary>
    private static bool HasConsumedPaste(IUiaNode node, string baseline, string text)
    {
        if (!TryReadContent(node, out var content) || content is null)
        {
            return false;
        }

        if (!content.Contains(text, StringComparison.Ordinal))
        {
            return false;
        }

        return !baseline.Contains(text, StringComparison.Ordinal)
            || !string.Equals(content, baseline, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the target's current content for paste-consumption detection.
    /// ValuePattern is the cheap edit-like read; TextPattern covers
    /// document/contenteditable targets. The content is compared only and never
    /// logged or put into an error.
    /// </summary>
    private static bool TryReadContent(IUiaNode? node, out string? content)
    {
        content = null;
        if (node is null)
        {
            return false;
        }

        if (node.TryGetValue(out var value) && value is not null)
        {
            content = value;
            return true;
        }

        if (node.TryGetText(out var text) && text is not null)
        {
            content = text;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The production queue drain: sends a no-op message to the window and
    /// waits for it to be processed, bounded by the paste deadline and skipped
    /// if the window is hung. This is only used when the target's content
    /// cannot be read directly.
    /// </summary>
    private static void DrainInputQueue(
        nint hwnd,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (hwnd == 0)
        {
            return;
        }

        var milliseconds = (uint)Math.Clamp(
            Math.Ceiling(Math.Max(0, timeout.TotalMilliseconds)),
            1d,
            int.MaxValue);

        SendMessageTimeout(
            hwnd,
            WmNull,
            0,
            0,
            SmtoBlock | SmtoAbortIfHung,
            milliseconds,
            out _);

        cancellationToken.ThrowIfCancellationRequested();
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hwnd,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);
}

/// <summary>
/// Waits, bounded, for a window to process its posted input when the paste
/// target's content cannot be read. The default implementation sends a no-op
/// message with <c>SendMessageTimeout</c>; tests substitute a deterministic
/// seam.
/// </summary>
public delegate void InputQueueDrain(
    nint hwnd,
    TimeSpan timeout,
    CancellationToken cancellationToken);
