namespace KennectComputer.Host.Actions;

/// <summary>
/// The host-side form of a shared <c>KeyRequest</c> (R9). Task 13 parses the
/// protocol JSON into this record; <see cref="Keys"/> holds a key or chord such
/// as <c>Enter</c>, <c>Ctrl+A</c>, or <c>Alt+F4</c> and must be parseable by
/// <see cref="Input.KeyParser"/>.
/// </summary>
public sealed record KeyRequest
{
    /// <summary>The key or chord to send; must not be empty.</summary>
    public string Keys { get; init; } = string.Empty;

    /// <summary>Preconditions evaluated against a fresh read before any input.</summary>
    public IReadOnlyList<ExpectPredicate> Expect { get; init; } = [];
}
