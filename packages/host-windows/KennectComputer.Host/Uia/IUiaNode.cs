using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Uia;

/// <summary>
/// A live accessibility element resolved from a snapshot node. Operations are
/// deliberately semantic: the host acts through UI Automation patterns where
/// the app supports them and reports <c>false</c> when a pattern is
/// unavailable, so callers can fall back (for example to a bounds-center
/// click) or report the limitation. The interface carries no Windows types, so
/// action tests and later platforms can substitute their own implementation.
/// </summary>
public interface IUiaNode
{
    /// <summary>Generic, platform-neutral role (button, edit, document, …).</summary>
    string Role { get; }

    string Name { get; }

    /// <summary>Platform accessibility identifier, when the app exposes one.</summary>
    string? Identifier { get; }

    /// <summary>Bounds in physical screen pixels.</summary>
    Rect Bounds { get; }

    bool Enabled { get; }

    bool Focused { get; }

    /// <summary>
    /// True when the element is a password field. Its text/value is never
    /// readable through this interface (DESIGN §8 hard rule 4).
    /// </summary>
    bool IsPassword { get; }

    /// <summary>Generic action names the live element supports.</summary>
    IReadOnlyList<string> Actions { get; }

    /// <summary>Activates the element through its invoke pattern.</summary>
    bool TryInvoke();

    /// <summary>Flips a toggle/check control.</summary>
    bool TryToggle();

    /// <summary>Selects a list, tab, or menu item.</summary>
    bool TrySelect();

    /// <summary>Expands a collapsible container.</summary>
    bool TryExpand();

    /// <summary>Moves keyboard focus to the element.</summary>
    bool TryFocus();

    /// <summary>Replaces the element's value; false when it is unsupported or read-only.</summary>
    bool TrySetValue(string value);

    /// <summary>Reads document-like text; false when no text pattern is available.</summary>
    bool TryGetText(out string? text);

    /// <summary>Reads an edit-like value; false when no value pattern is available.</summary>
    bool TryGetValue(out string? value);
}
