namespace KennectComputer.Host.Input;

/// <summary>
/// The high-level input seam used by click, type, key, and scroll actions
/// (DESIGN §6.2). Implementations translate these calls into ordered low-level
/// events; tests substitute a fake implementation so no real input is ever
/// injected.
/// </summary>
public interface IInputSender
{
    /// <summary>Moves the pointer to an absolute physical screen point.</summary>
    void MoveTo(int x, int y);

    /// <summary>
    /// Clicks at the current pointer position, pressing and releasing
    /// <paramref name="button"/> <paramref name="count"/> times in order.
    /// </summary>
    void Click(MouseButton button, int count);

    /// <summary>
    /// Turns the wheel by <paramref name="delta"/>. Negative values scroll down
    /// or left; each notch is 120.
    /// </summary>
    void Scroll(int delta, bool horizontal);

    /// <summary>Presses a virtual key without releasing it.</summary>
    void KeyDown(ushort virtualKey);

    /// <summary>Releases a virtual key.</summary>
    void KeyUp(ushort virtualKey);

    /// <summary>
    /// Types text one UTF-16 code unit at a time through Unicode injection,
    /// independently of the active keyboard layout.
    /// </summary>
    void UnicodeText(string text);
}

/// <summary>
/// The OS injection seam behind <see cref="WindowsInputSender"/>. Unit tests
/// substitute a fake so the event mapping, order, and fields can be verified
/// without touching the real keyboard or mouse.
/// </summary>
public interface IInputApi
{
    /// <summary>
    /// Sends the events in order. Implementations must reject an incomplete
    /// send rather than silently dropping input.
    /// </summary>
    void Send(IReadOnlyList<InputEvent> events);
}
