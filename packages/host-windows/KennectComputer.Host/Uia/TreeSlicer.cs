using System.Text;

using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Uia;

/// <summary>
/// The compact view of a tree: protocol nodes in traversal order, the number of
/// raw nodes surveyed, whether a cap cut the tree, and the node-ID to source
/// mapping kept for later node actions.
/// </summary>
public sealed class SliceResult
{
    public IReadOnlyList<Node> Nodes { get; init; } = [];

    public int TotalNodes { get; init; }

    public bool Truncated { get; init; }

    public IReadOnlyDictionary<string, TreeModel> NodeMap { get; init; }
        = new Dictionary<string, TreeModel>(StringComparer.Ordinal);
}

/// <summary>
/// Turns a raw accessibility tree into the compact snapshot outline (DESIGN §5):
/// interactive nodes, text, and named context containers are kept; offscreen and
/// disabled subtrees are dropped; purely decorative unnamed containers are
/// collapsed and their children hoisted. Names are sanitized before they cross
/// the protocol boundary, and every kept node gets an ephemeral, generation-
/// scoped ID. Document subtrees are allowed to exceed the generic depth cap
/// (R7/R11); the node cap always applies (R21).
/// </summary>
public static class TreeSlicer
{
    public const int MaxNameLength = 256;

    private static readonly HashSet<string> KeptRoles = new(StringComparer.Ordinal)
    {
        "button",
        "edit",
        "document",
        "hyperlink",
        "checkbox",
        "radiobutton",
        "combobox",
        "listitem",
        "menuitem",
        "tabitem",
        "treeitem",
        "slider",
        "spinner",
        "text",
    };

    public static SliceResult Slice(TreeModel tree, SnapshotOptions options)
        => Slice(tree, options, generation: 0);

    public static SliceResult Slice(TreeModel tree, SnapshotOptions options, int generation)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(options);

        var nodes = new List<Node>();
        var nodeMap = new Dictionary<string, TreeModel>(StringComparer.Ordinal);
        var totalNodes = 0;
        var truncated = tree.Truncated;

        void Visit(TreeModel node, int depth, bool insideDocument, bool isRoot)
        {
            totalNodes++;

            var role = NormalizeRole(node.Role);
            var inDocument = insideDocument
                || string.Equals(role, "document", StringComparison.Ordinal);
            var depthLimit = inDocument
                ? Math.Max(options.MaxDepth, SnapshotOptions.DocumentMaxDepth)
                : options.MaxDepth;

            if (depth > depthLimit)
            {
                truncated = true;
                return;
            }

            // Offscreen and disabled subtrees are useless to the model; skipping
            // the subtree also keeps a large hidden tree from consuming the cap.
            if (node.Offscreen || !node.Enabled)
            {
                return;
            }

            var name = SanitizeName(node.Name);
            var actions = SanitizeActions(node.Actions);

            // R8: an unnamed Group with Invoke/TextEdit/SelectionItem patterns
            // is a real target. Scroll-only patterns are not: nearly every
            // element supports ScrollItem, and treating that as actionable
            // would un-collapse every decorative container.
            var semantic = KeptRoles.Contains(role) || IsActionable(actions);

            if (semantic && !string.Equals(role, "text", StringComparison.Ordinal))
            {
                actions.Add("focus");
            }

            // Empty text nodes are layout noise; unnamed containers are
            // collapsed below, but a text node with no content has nothing to
            // collapse to.
            var unnamedText = string.Equals(role, "text", StringComparison.Ordinal)
                && name.Length == 0;

            var keep = !unnamedText
                && !(isRoot && string.Equals(role, "window", StringComparison.Ordinal))
                && (semantic || name.Length > 0);

            if (keep)
            {
                if (nodes.Count >= options.MaxNodes)
                {
                    truncated = true;
                }
                else
                {
                    var nodeId = NodeId.Create(generation, nodes.Count + 1);
                    nodes.Add(new Node
                    {
                        NodeId = nodeId,
                        Role = role,
                        Name = name,
                        Identifier = string.IsNullOrWhiteSpace(node.Identifier)
                            ? null
                            : SanitizeName(node.Identifier),
                        Bounds = ClampBounds(node.Bounds),
                        Enabled = true,
                        Focused = node.Focused,
                        Actions = [.. actions],
                    });
                    nodeMap[nodeId] = node;
                }
            }

            foreach (var child in node.Children)
            {
                Visit(child, depth + 1, inDocument, isRoot: false);
            }
        }

        Visit(tree, depth: 0, insideDocument: false, isRoot: true);

        return new SliceResult
        {
            Nodes = nodes,
            TotalNodes = totalNodes,
            Truncated = truncated,
            NodeMap = nodeMap,
        };
    }

    /// <summary>
    /// Collapses all whitespace/control runs to one space, trims, and caps the
    /// length. Names are untrusted app content and must not carry newlines into
    /// the outline or the log surface.
    /// </summary>
    public static string SanitizeName(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxNameLength));
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
            if (builder.Length >= MaxNameLength)
            {
                break;
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static bool IsActionable(List<string> actions)
        => actions.Exists(action =>
            !string.Equals(action, "scroll", StringComparison.Ordinal)
            && !string.Equals(action, "scrollIntoView", StringComparison.Ordinal));

    private static List<string> SanitizeActions(IReadOnlyList<string>? actions)
    {
        var result = new List<string>();
        if (actions is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            var sanitized = SanitizeName(action);
            if (sanitized.Length == 0)
            {
                continue;
            }

            if (seen.Add(sanitized))
            {
                result.Add(sanitized);
            }
        }

        return result;
    }

    private static string NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return "unknown";
        }

        return SanitizeName(role).ToLowerInvariant();
    }

    private static Rect ClampBounds(Rect bounds) => new()
    {
        X = bounds.X,
        Y = bounds.Y,
        Width = Math.Max(0, bounds.Width),
        Height = Math.Max(0, bounds.Height),
    };
}
