using System.Runtime.ExceptionServices;

using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Actions;

/// <summary>
/// Sends a key or chord to the selected window (DESIGN §6.5).
///
/// The selected window is re-resolved first and the key string is parsed next,
/// so an unknown name never reaches input. Expectations run against a fresh
/// accessibility read and the window is verified foreground before any event.
/// The chord presses its modifiers down in the written order, presses and
/// releases the key, and releases the modifiers in reverse. Cancellation is
/// checked once before the chord, never between its events, and any failure
/// after a modifier went down releases every modifier that was pressed, so a
/// partial send cannot leave the system with a stuck modifier. The action never
/// touches the clipboard and reports <c>method: "keys"</c>.
///
/// Action failures are raised as <see cref="ActionFailedException"/>; input-send
/// failures and cancellation propagate unchanged.
/// </summary>
public sealed class KeyAction
{
    private readonly ActionPreflight _preflight;
    private readonly IInputSender _input;

    public KeyAction(ActionPreflight preflight, IInputSender input)
    {
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public async Task<ActionResult> ExecuteAsync(
        KeyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = _preflight.ResolveSelectedWindow("sending keys");

        if (!KeyParser.TryParse(request.Keys, out var chord))
        {
            throw Failure(
                ErrorCode.InvalidRequest,
                "The keys value is not a known key or chord; use a key name such as "
                + "Enter, Tab, or F1, or a chord such as Ctrl+A.");
        }

        // The window identity is verified above; now check the caller's
        // preconditions against one fresh read, before any focus or input.
        await _preflight
            .CheckExpectationsAsync(resolved, request.Expect, cancellationToken)
            .ConfigureAwait(false);

        await _preflight
            .FocusAsync(
                resolved.Hwnd,
                "Windows refused to bring the window to the foreground; no keys were sent.",
                cancellationToken)
            .ConfigureAwait(false);

        // Checked once, before the chord: aborting between the modifier press
        // and the key press could strand the modifier down.
        cancellationToken.ThrowIfCancellationRequested();
        SendChord(chord);

        return new ActionResult
        {
            Window = resolved.Info,
            Action = "key",
            Method = "keys",
            NeedsObservation = true,
        };
    }

    /// <summary>
    /// Sends the chord and releases what was pressed no matter how the send
    /// ends. Every release is attempted even when one fails, and the first
    /// failure (the chord's, else a release's) propagates unchanged.
    /// </summary>
    private void SendChord(KeyChord chord)
    {
        var pressed = new List<ushort>(chord.Modifiers.Count);
        Exception? failure = null;

        try
        {
            foreach (var modifier in chord.Modifiers)
            {
                _input.KeyDown(modifier);
                pressed.Add(modifier);
            }

            _input.KeyDown(chord.Key);
            _input.KeyUp(chord.Key);
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

    private static ActionFailedException Failure(ErrorCode code, string message)
        => new(code, message);
}
