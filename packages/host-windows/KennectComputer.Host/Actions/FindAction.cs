using KennectComputer.Host.Control;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Actions;

/// <summary>
/// The host-side form of a shared <c>FindRequest</c> (R9). Task 13 parses the
/// protocol JSON into this record. Both filters are optional; with neither, a
/// find returns the sliced outline.
/// </summary>
public sealed record FindRequest
{
    /// <summary>Case-insensitive substring matched against a node's name or value.</summary>
    public string? Text { get; init; }

    /// <summary>Exact, lower-case role such as <c>button</c> or <c>edit</c>.</summary>
    public string? Role { get; init; }

    /// <summary>Maximum matches to return; defaults to 50 and is capped at 500.</summary>
    public int? MaxResults { get; init; }
}

/// <summary>
/// The host-side form of a shared <c>FindResult</c> (R10). Matches carry the
/// protocol <see cref="Node"/> shape; the host-internal value used for
/// matching never crosses this boundary.
/// </summary>
public sealed record FindResult
{
    public WindowInfo Window { get; init; } = new();

    /// <summary>The snapshot generation the returned node IDs belong to.</summary>
    public int Generation { get; init; }

    /// <summary>Actionable protocol nodes matching the filters, in tree order.</summary>
    public List<Node> Matches { get; init; } = [];

    /// <summary>Raw accessibility nodes surveyed by the walk.</summary>
    public int Scanned { get; init; }

    /// <summary>True when the walk was capped or more matches existed than returned.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Searches the selected window's live accessibility tree (DESIGN §6.7).
///
/// The selected window is re-resolved through the shared preflight resolver,
/// so a missing selection is <c>INVALID_REQUEST</c> and a dead window is
/// <c>WINDOW_GONE</c>. A fresh walk produces the outline under the find depth
/// and node budgets; the slicer assigns a new generation to the kept nodes,
/// and the matches are filtered by a case-insensitive substring over the name
/// or the host-internal value and/or by exact role. The resulting slice is
/// saved as the window's snapshot state with the current window bounds and no
/// image: later node actions can use the match IDs immediately, while
/// image-space coordinates require a new snapshot with an image (R5).
///
/// No matches is a successful empty result. The action is read-only: it never
/// sends input, focuses the window, or evaluates expectations.
/// </summary>
public sealed class FindAction
{
    public const int DefaultMaxResults = 50;
    public const int MaxResultsCap = 500;
    public const int WalkMaxDepth = 20;
    public const int WalkMaxNodes = 5000;

    private readonly IUiaWalker _walker;
    private readonly UiaSnapshotter _snapshotter;
    private readonly SnapshotStore _snapshots;
    private readonly SelectionStore _selection;
    private readonly WindowRegistry _registry;

    public FindAction(
        IUiaWalker walker,
        UiaSnapshotter snapshotter,
        SnapshotStore snapshots,
        SelectionStore selection,
        WindowRegistry registry)
    {
        _walker = walker ?? throw new ArgumentNullException(nameof(walker));
        _snapshotter = snapshotter ?? throw new ArgumentNullException(nameof(snapshotter));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public Task<FindResult> ExecuteAsync(
        FindRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = ActionPreflight.ResolveSelectedWindow(
            _selection,
            _registry,
            "finding nodes");

        var options = new SnapshotOptions
        {
            MaxDepth = WalkMaxDepth,
            MaxNodes = WalkMaxNodes,
            Mode = SnapshotMode.A11y,
        };

        cancellationToken.ThrowIfCancellationRequested();
        var tree = _walker.Walk(resolved.Hwnd, options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // A find always replaces the window's state, even when the walk yields
        // nothing: the old generation (and its image metadata) must not stay
        // actionable after the caller has searched afresh.
        var generation = _snapshotter.NextGeneration();
        var slice = tree is null
            ? new SliceResult()
            : TreeSlicer.Slice(tree, options, generation);

        var text = string.IsNullOrEmpty(request.Text) ? null : request.Text;
        var role = string.IsNullOrWhiteSpace(request.Role)
            ? null
            : request.Role.Trim().ToLowerInvariant();
        var limit = Math.Clamp(request.MaxResults ?? DefaultMaxResults, 1, MaxResultsCap);

        var filtered = slice.Nodes
            .Where(node => Matches(node, slice.NodeMap, text, role))
            .ToList();
        var matches = filtered.Take(limit).ToList();
        var truncated = slice.Truncated || filtered.Count > limit;

        // The snapshot state keeps the whole sliced outline, not only the
        // matches, so a later node action can use any ID observed in this
        // generation. Image is deliberately null (R5).
        _snapshots.Save(
            resolved.Info.WindowId,
            new Snapshot
            {
                Generation = generation,
                Nodes = [.. slice.Nodes],
                TotalNodes = slice.TotalNodes,
                Truncated = slice.Truncated,
            },
            slice.NodeMap,
            resolved.Info.Bounds);

        return Task.FromResult(new FindResult
        {
            Window = resolved.Info,
            Generation = generation,
            Matches = matches,
            Scanned = slice.TotalNodes,
            Truncated = truncated,
        });
    }

    private static bool Matches(
        Node node,
        IReadOnlyDictionary<string, TreeModel> nodeMap,
        string? text,
        string? role)
    {
        if (role is not null && !string.Equals(node.Role, role, StringComparison.Ordinal))
        {
            return false;
        }

        if (text is null)
        {
            return true;
        }

        if (node.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The value is host-internal (never serialized): it lets find match an
        // edit's content even when the element has no accessible name. A
        // password field's value is never captured or matched (DESIGN §8).
        return nodeMap.TryGetValue(node.NodeId, out var model)
            && !model.IsPassword
            && model.Value is not null
            && model.Value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}
