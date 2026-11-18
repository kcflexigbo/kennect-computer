using System.Globalization;

namespace KennectComputer.Host.Input;

/// <summary>
/// A parsed key or chord: the modifier virtual keys in the order they were
/// written (so they are pressed in that order and released in reverse) and the
/// key virtual key that is pressed and released between them.
/// </summary>
public readonly record struct KeyChord(IReadOnlyList<ushort> Modifiers, ushort Key);

/// <summary>
/// Parses the shared protocol's <c>keys</c> string into a <see cref="KeyChord"/>
/// (DESIGN §6.5).
///
/// Accepted forms are a documented name (<c>Enter</c>, <c>Tab</c>,
/// <c>Escape</c>, <c>F1</c>–<c>F24</c>, arrows, <c>Home</c>, <c>End</c>,
/// <c>PageUp</c>, <c>PageDown</c>, <c>Delete</c>, <c>Backspace</c>,
/// <c>Space</c>), a single ASCII letter or digit, or those keys preceded by
/// modifiers joined with <c>+</c> (<c>Ctrl+A</c>, <c>Ctrl+Shift+S</c>,
/// <c>Alt+F4</c>, <c>Win+R</c>). Names and modifiers are case-insensitive;
/// <c>Ctrl</c> and <c>Control</c> are the same modifier. Empty strings, bare
/// modifiers, empty tokens (<c>Ctrl+</c>), and unknown keys are rejected, so an
/// unrecognized value becomes <c>INVALID_REQUEST</c> and never reaches input.
/// </summary>
public static class KeyParser
{
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkLeftWin = 0x5B;
    private const ushort VkF1 = 0x70;
    private const int LastFunctionKey = 24;

    private static readonly Dictionary<string, ushort> ModifierKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Ctrl"] = VkControl,
            ["Control"] = VkControl,
            ["Shift"] = VkShift,
            ["Alt"] = VkMenu,
            ["Win"] = VkLeftWin,
            ["Windows"] = VkLeftWin,
        };

    private static readonly Dictionary<string, ushort> NamedKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = 0x0D,
            ["Return"] = 0x0D,
            ["Tab"] = 0x09,
            ["Escape"] = 0x1B,
            ["Esc"] = 0x1B,
            ["Up"] = 0x26,
            ["ArrowUp"] = 0x26,
            ["Down"] = 0x28,
            ["ArrowDown"] = 0x28,
            ["Left"] = 0x25,
            ["ArrowLeft"] = 0x25,
            ["Right"] = 0x27,
            ["ArrowRight"] = 0x27,
            ["Home"] = 0x24,
            ["End"] = 0x23,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["Delete"] = 0x2E,
            ["Backspace"] = 0x08,
            ["Space"] = 0x20,
        };

    /// <summary>
    /// Parses a single modifier name (Ctrl/Control, Shift, Alt, Win/Windows)
    /// into its virtual key. Click modifiers are names, not chords.
    /// </summary>
    public static bool TryParseModifier(string? value, out ushort virtualKey)
    {
        virtualKey = 0;

        return !string.IsNullOrWhiteSpace(value)
            && ModifierKeys.TryGetValue(value.Trim(), out virtualKey);
    }

    /// <summary>
    /// Parses <paramref name="value"/> into a chord. Returns false for an empty
    /// or whitespace value, a bare modifier, an empty token, or an unknown key
    /// name; <paramref name="chord"/> is then the default value.
    /// </summary>
    public static bool TryParse(string? value, out KeyChord chord)
    {
        chord = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Split never returns an empty array, and the value is non-blank, so
        // the last token is always present and only its trimmed content can be
        // empty (a trailing '+').
        var tokens = value.Split('+');
        var keyName = tokens[^1].Trim();

        // A bare modifier or a trailing '+' is not a chord; the key may not be
        // a modifier because nothing would be pressed and released.
        if (keyName.Length == 0 || ModifierKeys.ContainsKey(keyName))
        {
            return false;
        }

        var modifiers = new List<ushort>(tokens.Length - 1);
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            var modifierName = tokens[index].Trim();
            if (!ModifierKeys.TryGetValue(modifierName, out var modifier))
            {
                return false;
            }

            modifiers.Add(modifier);
        }

        if (!TryKey(keyName, out var key))
        {
            return false;
        }

        chord = new KeyChord([.. modifiers], key);
        return true;
    }

    private static bool TryKey(string name, out ushort virtualKey)
        => NamedKeys.TryGetValue(name, out virtualKey)
            || TryFunctionKey(name, out virtualKey)
            || TryCharacter(name, out virtualKey);

    private static bool TryFunctionKey(string name, out ushort virtualKey)
    {
        virtualKey = 0;

        if (name.Length is < 2 or > 3 || (name[0] != 'F' && name[0] != 'f'))
        {
            return false;
        }

        if (!int.TryParse(
                name.AsSpan(1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number)
            || number is < 1 or > LastFunctionKey)
        {
            return false;
        }

        virtualKey = (ushort)(VkF1 + number - 1);
        return true;
    }

    private static bool TryCharacter(string name, out ushort virtualKey)
    {
        virtualKey = 0;

        if (name.Length != 1)
        {
            return false;
        }

        var character = name[0];
        if (character is >= 'a' and <= 'z')
        {
            virtualKey = (ushort)(character - ('a' - 'A'));
            return true;
        }

        if (character is >= 'A' and <= 'Z' || character is >= '0' and <= '9')
        {
            virtualKey = character;
            return true;
        }

        return false;
    }
}
