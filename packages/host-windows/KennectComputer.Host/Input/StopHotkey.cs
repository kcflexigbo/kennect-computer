using System.Runtime.InteropServices;
using System.Windows.Forms;

using KennectComputer.Host.Control;

namespace KennectComputer.Host.Input;

/// <summary>The <c>MOD_*</c> flags accepted by <c>RegisterHotKey</c>.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// The <c>RegisterHotKey</c> seam. Tests substitute a fake so no global hotkey
/// is ever registered by the automated suites (R28).
/// </summary>
public interface IHotkeyRegistrar
{
    bool Register(nint hwnd, int id, HotkeyModifiers modifiers, Keys key);

    bool Unregister(nint hwnd, int id);
}

/// <summary>Win32 implementation of the hotkey registrar.</summary>
public sealed class Win32HotkeyRegistrar : IHotkeyRegistrar
{
    public bool Register(nint hwnd, int id, HotkeyModifiers modifiers, Keys key)
        => RegisterHotKey(hwnd, id, (uint)modifiers, (uint)key);

    public bool Unregister(nint hwnd, int id)
        => UnregisterHotKey(hwnd, id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}

/// <summary>
/// The global stop hotkey (default <c>Ctrl+Alt+Shift+Esc</c>). It is registered
/// on the hidden window owned by the tray STA thread; a matching
/// <c>WM_HOTKEY</c> is consumed and calls <see cref="ControlSession.Stop"/>
/// (DESIGN §8). Registration failure is reported through
/// <see cref="IsRegistered"/> and must never crash the host.
/// </summary>
public sealed class StopHotkey : IDisposable
{
    /// <summary>Identifier passed to <c>RegisterHotKey</c>; "KC" in ASCII.</summary>
    public const int DefaultId = 0x4B43;

    private readonly ControlSession _control;
    private readonly IHotkeyRegistrar _registrar;
    private readonly HotkeyModifiers _modifiers;
    private readonly Keys _key;
    private readonly bool _configured;
    private nint _hwnd;
    private int _id;
    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// Parses <paramref name="shortcut"/> (for example
    /// <c>Ctrl+Alt+Shift+Esc</c>) and holds the registration intent. Nothing is
    /// registered until <see cref="Register"/> runs on the window's thread.
    /// </summary>
    public StopHotkey(ControlSession control, IHotkeyRegistrar registrar, string shortcut)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        Shortcut = shortcut ?? string.Empty;
        _configured = TryParse(shortcut, out _modifiers, out _key);
    }

    /// <summary>The configured shortcut text, for the tray status.</summary>
    public string Shortcut { get; }

    /// <summary>True when the configured shortcut parsed.</summary>
    public bool IsConfigured => _configured;

    /// <summary>True when the OS accepted the registration.</summary>
    public bool IsRegistered => _registered;

    /// <summary>
    /// Registers the hotkey on <paramref name="hwnd"/>. Returns false (without
    /// throwing) when the shortcut is invalid, already registered, or owned by
    /// another application.
    /// </summary>
    public bool Register(nint hwnd, int id)
    {
        if (_registered)
        {
            return true;
        }

        if (!_configured || _disposed)
        {
            return false;
        }

        _hwnd = hwnd;
        _id = id;
        _registered = _registrar.Register(hwnd, id, _modifiers, _key);
        return _registered;
    }

    /// <summary>
    /// Handles a <c>WM_HOTKEY</c> id. Returns true when the message belongs to
    /// the stop key and was consumed; the session is then stopped (R35).
    /// </summary>
    public bool HandleMessage(int id)
    {
        if (!_registered || id != _id)
        {
            return false;
        }

        _control.Stop();
        return true;
    }

    public static bool TryParse(
        string? shortcut,
        out HotkeyModifiers modifiers,
        out Keys key)
    {
        modifiers = HotkeyModifiers.None;
        key = Keys.None;

        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return false;
        }

        var parts = shortcut.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (var index = 0; index < parts.Length - 1; index++)
        {
            switch (parts[index].ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    return false;
            }
        }

        var keyText = parts[^1];
        if (keyText.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        // A single digit names D0..D9; rejecting the raw numeric enum cast keeps
        // "12" from silently becoming an unrelated virtual-key value.
        if (keyText.Length == 1 && char.IsAsciiDigit(keyText[0]))
        {
            key = (Keys)((int)Keys.D0 + (keyText[0] - '0'));
            return true;
        }

        if (keyText.All(char.IsAsciiDigit))
        {
            return false;
        }

        // The config spells the Windows key "Esc"; the enum member is Escape.
        if (keyText.Equals("Esc", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Escape;
            return true;
        }

        return Enum.TryParse(keyText, ignoreCase: true, out key) && key != Keys.None;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_registered)
        {
            _registrar.Unregister(_hwnd, _id);
            _registered = false;
        }
    }
}
