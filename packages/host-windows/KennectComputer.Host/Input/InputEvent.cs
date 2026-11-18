namespace KennectComputer.Host.Input;

/// <summary>
/// The mouse buttons an action can press. These correspond to the shared
/// protocol's <c>"left" | "middle" | "right"</c> button strings, which the
/// request parser maps here.
/// </summary>
public enum MouseButton
{
    Left,
    Middle,
    Right,
}

/// <summary>The kind of low-level input event carried by an <see cref="InputEvent"/>.</summary>
public enum InputEventKind
{
    /// <summary>Move the pointer. Absolute coordinates unless declared relative.</summary>
    MouseMove,

    MouseButtonDown,

    MouseButtonUp,

    /// <summary>Turn the wheel. Negative deltas scroll down or left.</summary>
    MouseWheel,

    KeyDown,

    KeyUp,

    /// <summary>Type one UTF-16 code unit through Unicode injection.</summary>
    Unicode,
}

/// <summary>Attributes that qualify an <see cref="InputEvent"/>.</summary>
[Flags]
public enum InputEventFlags
{
    None = 0,

    /// <summary><see cref="InputEvent.X"/>/<see cref="InputEvent.Y"/> are physical screen pixels.</summary>
    Absolute = 1 << 0,

    /// <summary>The wheel delta applies to the horizontal wheel.</summary>
    Horizontal = 1 << 1,

    /// <summary>The event injects a Unicode code unit rather than a virtual key.</summary>
    Unicode = 1 << 2,
}

/// <summary>
/// One low-level input event, before it is mapped to a platform record.
///
/// The high-level <see cref="IInputSender"/> produces these events and hands
/// them to an <see cref="IInputApi"/>; only the real API translates them to
/// Win32 <c>INPUT</c> records, which keeps the mapping testable without
/// injecting input.
/// </summary>
/// <param name="Kind">Which kind of event this is.</param>
/// <param name="X">Absolute or relative pointer X, depending on <paramref name="Flags"/>.</param>
/// <param name="Y">Absolute or relative pointer Y, depending on <paramref name="Flags"/>.</param>
/// <param name="Button">The button pressed by a button event.</param>
/// <param name="Delta">The wheel delta for a wheel event (multiples of 120).</param>
/// <param name="VirtualKey">The virtual-key code for a key event.</param>
/// <param name="Unicode">The UTF-16 code unit for a Unicode event.</param>
/// <param name="Flags">Attributes that qualify the event.</param>
public sealed record InputEvent(
    InputEventKind Kind,
    int X = 0,
    int Y = 0,
    MouseButton Button = MouseButton.Left,
    int Delta = 0,
    ushort VirtualKey = 0,
    char Unicode = '\0',
    InputEventFlags Flags = InputEventFlags.None);
