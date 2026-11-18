namespace KennectComputer.Host.Actions;

/// <summary>How typed text reaches the control (DESIGN §6.4).</summary>
public enum TypeMethod
{
    /// <summary>
    /// Save the clipboard, set the text, send Ctrl+V, and restore the saved
    /// text. The default because it is fast and reliable for CJK and long text.
    /// </summary>
    Paste,

    /// <summary>Set the accessibility value directly where the node supports it.</summary>
    Value,

    /// <summary>Type every UTF-16 code unit through Unicode input; slower fallback.</summary>
    Keys,
}

/// <summary>
/// The host-side form of a shared <c>TypeRequest</c> (R9). Task 13 parses the
/// protocol JSON into this record; the defaults mirror the shared schema: paste
/// and no node (text goes to the focused control of the selected window).
/// <see cref="Text"/> must be non-empty, and <see cref="TypeMethod.Value"/>
/// requires a <see cref="NodeId"/> to set the value on.
/// </summary>
public sealed record TypeRequest
{
    /// <summary>The text to type; must be non-empty.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>How the text reaches the control; paste by default.</summary>
    public TypeMethod Method { get; init; } = TypeMethod.Paste;

    /// <summary>The snapshot node to focus and type into; valid only for its generation.</summary>
    public string? NodeId { get; init; }

    /// <summary>Preconditions evaluated against a fresh read before any input.</summary>
    public IReadOnlyList<ExpectPredicate> Expect { get; init; } = [];
}
