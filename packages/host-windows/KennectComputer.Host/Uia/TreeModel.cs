using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Uia;

/// <summary>
/// The raw accessibility tree as read from the platform, before filtering and
/// node-ID assignment. Roles are lower-case generic vocabulary (button, edit,
/// document, …) so the model is not tied to Windows control names.
/// </summary>
public sealed record TreeModel
{
    public string Role { get; init; } = "unknown";

    public string Name { get; init; } = string.Empty;

    public string? Identifier { get; init; }

    /// <summary>
    /// The element's text/value at capture time, used by expect/find/wait for
    /// content matching. Host-internal only: the protocol <see cref="Node"/>
    /// shape is frozen and this is never serialized or copied into it. Always
    /// null for a password field (DESIGN §8 hard rule 4).
    /// </summary>
    internal string? Value { get; init; }

    /// <summary>
    /// True when the element is a password field. The walker never captures its
    /// value, and find/wait/expect must not match against it.
    /// </summary>
    public bool IsPassword { get; init; }

    public Rect Bounds { get; init; } = new();

    public bool Enabled { get; init; } = true;

    public bool Offscreen { get; init; }

    public bool Focused { get; init; }

    /// <summary>True when a visible element exposes <c>WindowPattern.IsModal</c>.</summary>
    public bool IsModal { get; init; }

    public IReadOnlyList<string> Actions { get; init; } = [];

    public IReadOnlyList<TreeModel> Children { get; init; } = [];

    /// <summary>
    /// Set by the walker when a depth or node cap stopped the walk, so the
    /// slicer can report truncation even when every walked node is kept.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Builds a deterministic tree for slicer tests: a nested chain of
    /// <paramref name="depth"/> levels where each level also carries
    /// <paramref name="childrenPerLevel"/> sibling nodes, so the node count is
    /// bounded (<c>depth × childrenPerLevel + 1</c>) instead of exponential.
    /// </summary>
    public static TreeModel Nested(int depth, int childrenPerLevel)
    {
        var siblings = Math.Max(1, childrenPerLevel);
        var index = 0;

        TreeModel Build(int level)
        {
            var children = new List<TreeModel>();
            if (level < depth)
            {
                children.Add(Build(level + 1));

                for (var sibling = 1; sibling < siblings; sibling++)
                {
                    children.Add(Leaf());
                }
            }

            return Leaf(children);
        }

        return Build(0);

        TreeModel Leaf(IReadOnlyList<TreeModel>? children = null) => new()
        {
            Role = "button",
            Name = $"node{++index}",
            Actions = ["press"],
            Children = children ?? [],
        };
    }
}
