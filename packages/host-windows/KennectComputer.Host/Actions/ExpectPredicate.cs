namespace KennectComputer.Host.Actions;

/// <summary>
/// A host-side precondition evaluated against a fresh accessibility read
/// immediately before input. Task 13 maps the shared protocol predicate JSON
/// onto these records; the shared TypeScript union is not represented here.
/// </summary>
public abstract record ExpectPredicate
{
    /// <summary>
    /// The stable name reported when this predicate is the first failure:
    /// <c>foreground</c>, <c>node:&lt;name&gt;</c>, or <c>text:&lt;contains&gt;</c>.
    /// </summary>
    public abstract string FailureName { get; }
}

/// <summary>The selected window must be the foreground window.</summary>
public sealed record ExpectForeground : ExpectPredicate
{
    public override string FailureName => "foreground";
}

/// <summary>
/// A node matching the supplied selector must exist. Supplied selector fields
/// are matched exactly; omitted fields are wildcards. When <see cref="Focused"/>
/// is set, the node's focused flag must match it.
/// </summary>
public sealed record ExpectNode : ExpectPredicate
{
    public string? Role { get; init; }

    public string? Name { get; init; }

    public string? Identifier { get; init; }

    public bool? Focused { get; init; }

    public override string FailureName => $"node:{Name ?? Identifier ?? Role ?? string.Empty}";
}

/// <summary>
/// A node matching the supplied selector must exist and its captured text/value
/// must contain <see cref="Contains"/> (ordinal, case-insensitive). Omitted
/// selector fields are wildcards.
/// </summary>
public sealed record ExpectText : ExpectPredicate
{
    public string? Role { get; init; }

    public string? Name { get; init; }

    public string? Identifier { get; init; }

    public required string Contains { get; init; }

    public override string FailureName => $"text:{Contains}";
}
