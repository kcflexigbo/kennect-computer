namespace KennectComputer.Host.Input;

/// <summary>
/// The clipboard seam used by paste-based typing (DESIGN §6.2). The type action
/// owns any save/restore around the primitive.
/// </summary>
public interface IClipboard
{
    /// <summary>
    /// True when the clipboard holds content a Unicode-text save/restore
    /// cannot preserve: an image, files, HTML, or ANSI-only text. Paste must
    /// refuse rather than empty the clipboard and restore nothing.
    /// </summary>
    bool HasUnsupportedContent();

    /// <summary>
    /// Returns the clipboard's Unicode text, or null when it holds no text.
    /// </summary>
    string? GetText();

    /// <summary>Replaces the clipboard's Unicode text.</summary>
    void SetText(string text);
}
