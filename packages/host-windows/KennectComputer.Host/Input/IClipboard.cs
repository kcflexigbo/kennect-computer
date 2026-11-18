namespace KennectComputer.Host.Input;

/// <summary>
/// The clipboard seam used by paste-based typing (DESIGN §6.2). The type action
/// owns any save/restore around the primitive.
/// </summary>
public interface IClipboard
{
    /// <summary>
    /// Returns the clipboard's Unicode text, or null when it holds no text.
    /// </summary>
    string? GetText();

    /// <summary>Replaces the clipboard's Unicode text.</summary>
    void SetText(string text);
}
