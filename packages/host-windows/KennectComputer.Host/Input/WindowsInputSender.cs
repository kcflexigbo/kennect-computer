using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KennectComputer.Host.Input;

/// <summary>
/// Maps <see cref="IInputSender"/> calls onto ordered <see cref="InputEvent"/>
/// batches and hands them to an <see cref="IInputApi"/> (DESIGN §6.2, §12).
/// Unit tests pass a fake API, so the mapping is verified without injecting
/// real input.
/// </summary>
public sealed class WindowsInputSender : IInputSender
{
    private readonly IInputApi _api;

    public WindowsInputSender(IInputApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public void MoveTo(int x, int y)
        => Send(new InputEvent(
            InputEventKind.MouseMove,
            X: x,
            Y: y,
            Flags: InputEventFlags.Absolute));

    public void Click(MouseButton button, int count)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                "A click count must be at least one.");
        }

        var events = new InputEvent[count * 2];
        for (var index = 0; index < count; index++)
        {
            events[index * 2] = new InputEvent(InputEventKind.MouseButtonDown, Button: button);
            events[(index * 2) + 1] = new InputEvent(InputEventKind.MouseButtonUp, Button: button);
        }

        Send(events);
    }

    public void Scroll(int delta, bool horizontal)
        => Send(new InputEvent(
            InputEventKind.MouseWheel,
            Delta: delta,
            Flags: horizontal ? InputEventFlags.Horizontal : InputEventFlags.None));

    public void KeyDown(ushort virtualKey)
        => Send(new InputEvent(InputEventKind.KeyDown, VirtualKey: virtualKey));

    public void KeyUp(ushort virtualKey)
        => Send(new InputEvent(InputEventKind.KeyUp, VirtualKey: virtualKey));

    public void UnicodeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var events = new InputEvent[text.Length];
        for (var index = 0; index < text.Length; index++)
        {
            events[index] = new InputEvent(
                InputEventKind.Unicode,
                Unicode: text[index],
                Flags: InputEventFlags.Unicode);
        }

        if (events.Length > 0)
        {
            Send(events);
        }
    }

    private void Send(params InputEvent[] events) => _api.Send(events);
}

/// <summary>
/// The production <see cref="IInputApi"/>: maps each abstract
/// <see cref="InputEvent"/> to a Win32 <c>INPUT</c> record and submits the batch
/// with <c>SendInput</c> (never <c>mouse_event</c>/<c>keybd_event</c>).
///
/// Absolute pointer coordinates are normalized to the virtual desktop so input
/// lands on the intended physical pixel across monitors. A send the OS does not
/// fully accept throws <see cref="Win32Exception"/> rather than leaving the
/// caller believing partial input was delivered.
/// </summary>
public sealed class SendInputApi : IInputApi
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseMoveFlag = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint Wheel = 0x0800;
    private const uint HorizontalWheel = 0x1000;
    private const uint VirtualDesk = 0x4000;
    private const uint Absolute = 0x8000;

    private const uint ExtendedKey = 0x0001;
    private const uint KeyUpFlag = 0x0002;
    private const uint UnicodeFlag = 0x0004;

    private const int VirtualScreenX = 76;
    private const int VirtualScreenY = 77;
    private const int VirtualScreenWidth = 78;
    private const int VirtualScreenHeight = 79;
    private const int AbsoluteCoordinateMax = 65535;

    // Arrow, navigation, Windows-key, and right-hand modifier keys need the
    // KEYEVENTF_EXTENDEDKEY bit to be distinguishable from their numpad twins.
    private static readonly HashSet<ushort> ExtendedVirtualKeys = new()
    {
        0x21, // Page Up
        0x22, // Page Down
        0x23, // End
        0x24, // Home
        0x25, // Left
        0x26, // Up
        0x27, // Right
        0x28, // Down
        0x2D, // Insert
        0x2E, // Delete
        0x5B, // Left Windows
        0x5C, // Right Windows
        0x5D, // Applications
        0x6F, // Numpad divide
        0x90, // Num Lock
        0xA3, // Right Ctrl
        0xA5, // Right Alt
    };

    public void Send(IReadOnlyList<InputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        var inputs = new List<Input>(events.Count);
        foreach (var inputEvent in events)
        {
            Append(inputs, inputEvent);
        }

        var nativeInputs = inputs.ToArray();
        var sent = SendInput((uint)nativeInputs.Length, nativeInputs, Marshal.SizeOf<Input>());
        if (sent != nativeInputs.Length)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"SendInput queued {sent} of {nativeInputs.Length} input records.");
        }
    }

    private static void Append(List<Input> inputs, InputEvent inputEvent)
    {
        switch (inputEvent.Kind)
        {
            case InputEventKind.MouseMove:
                inputs.Add(MoveEvent(inputEvent));
                break;
            case InputEventKind.MouseButtonDown:
                inputs.Add(MouseEvent(ButtonFlag(inputEvent.Button, down: true)));
                break;
            case InputEventKind.MouseButtonUp:
                inputs.Add(MouseEvent(ButtonFlag(inputEvent.Button, down: false)));
                break;
            case InputEventKind.MouseWheel:
                inputs.Add(MouseEvent(
                    inputEvent.Flags.HasFlag(InputEventFlags.Horizontal) ? HorizontalWheel : Wheel,
                    unchecked((uint)inputEvent.Delta)));
                break;
            case InputEventKind.KeyDown:
                inputs.Add(KeyEvent(inputEvent.VirtualKey, down: true));
                break;
            case InputEventKind.KeyUp:
                inputs.Add(KeyEvent(inputEvent.VirtualKey, down: false));
                break;
            case InputEventKind.Unicode:
                // One code unit becomes a key-down/key-up pair, matching how
                // KEYEVENTF_UNICODE text is normally injected.
                inputs.Add(UnicodeEvent(inputEvent.Unicode, down: true));
                inputs.Add(UnicodeEvent(inputEvent.Unicode, down: false));
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(inputEvent),
                    inputEvent.Kind,
                    "Unknown input event kind.");
        }
    }

    private static Input MoveEvent(InputEvent inputEvent)
    {
        var x = inputEvent.X;
        var y = inputEvent.Y;
        var flags = MouseMoveFlag;
        if (inputEvent.Flags.HasFlag(InputEventFlags.Absolute))
        {
            (x, y) = ToVirtualDesktop(inputEvent.X, inputEvent.Y);
            flags |= Absolute | VirtualDesk;
        }

        return MouseEvent(flags, x: x, y: y);
    }

    private static Input MouseEvent(uint flags, uint mouseData = 0, int x = 0, int y = 0)
        => new()
        {
            Type = InputMouse,
            Data = new InputData
            {
                Mouse = new MouseInput
                {
                    Dx = x,
                    Dy = y,
                    MouseData = mouseData,
                    Flags = flags,
                },
            },
        };

    private static Input KeyEvent(ushort virtualKey, bool down)
    {
        var flags = ExtendedVirtualKeys.Contains(virtualKey) ? ExtendedKey : 0u;
        if (!down)
        {
            flags |= KeyUpFlag;
        }

        return KeyboardEvent(virtualKey, scanCode: 0, flags);
    }

    private static Input UnicodeEvent(char codeUnit, bool down)
    {
        var flags = UnicodeFlag;
        if (!down)
        {
            flags |= KeyUpFlag;
        }

        return KeyboardEvent(0, codeUnit, flags);
    }

    private static Input KeyboardEvent(ushort virtualKey, ushort scanCode, uint flags)
        => new()
        {
            Type = InputKeyboard,
            Data = new InputData
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = scanCode,
                    Flags = flags,
                },
            },
        };

    private static uint ButtonFlag(MouseButton button, bool down) => button switch
    {
        MouseButton.Left => down ? LeftDown : LeftUp,
        MouseButton.Middle => down ? MiddleDown : MiddleUp,
        MouseButton.Right => down ? RightDown : RightUp,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button."),
    };

    private static (int X, int Y) ToVirtualDesktop(int x, int y)
    {
        var left = GetSystemMetrics(VirtualScreenX);
        var top = GetSystemMetrics(VirtualScreenY);
        var width = GetSystemMetrics(VirtualScreenWidth);
        var height = GetSystemMetrics(VirtualScreenHeight);

        return (Normalize(x, left, width), Normalize(y, top, height));
    }

    private static int Normalize(int value, int origin, int extent)
    {
        if (extent <= 1)
        {
            return 0;
        }

        var scaled = (int)Math.Round(
            (value - origin) * (double)AbsoluteCoordinateMax / (extent - 1),
            MidpointRounding.AwayFromZero);
        return Math.Clamp(scaled, 0, AbsoluteCoordinateMax);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;

        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;

        public int Dy;

        public uint MouseData;

        public uint Flags;

        public uint Time;

        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;

        public ushort ScanCode;

        public uint Flags;

        public uint Time;

        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] Input[] inputs, int sizeOfInput);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
