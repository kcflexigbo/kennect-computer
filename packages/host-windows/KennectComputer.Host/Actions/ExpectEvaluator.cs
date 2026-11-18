using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Actions;

/// <summary>
/// Outcome of evaluating an expect list. <see cref="FailedPredicate"/> is null
/// on success; otherwise it names the first failing predicate so the action can
/// return <c>EXPECTATION_FAILED</c> without sending input.
/// </summary>
public sealed record ExpectResult
{
    public bool Ok { get; init; }

    public string? FailedPredicate { get; init; }

    public static ExpectResult Success { get; } = new() { Ok = true };

    public static ExpectResult Failed(string predicate)
        => new() { Ok = false, FailedPredicate = predicate };
}

/// <summary>
/// Evaluates pre-action expectations against one fresh accessibility read, so a
/// mutating action never sends input to a window whose state changed after the
/// caller's snapshot.
///
/// Predicates are evaluated in order and the first failure stops evaluation.
/// The tree is walked at most once per call and shared by every node/text
/// predicate; a foreground-only or empty list does not walk at all. The walk
/// uses <see cref="SnapshotOptions"/> defaults, requests no image, and never
/// touches the snapshot store.
/// </summary>
public sealed class ExpectEvaluator
{
    private readonly IUiaWalker _walker;
    private readonly IForegroundApi _foreground;

    public ExpectEvaluator(IUiaWalker walker, IForegroundApi foreground)
    {
        _walker = walker ?? throw new ArgumentNullException(nameof(walker));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
    }

    public Task<ExpectResult> EvaluateAsync(
        nint hwnd,
        IReadOnlyList<ExpectPredicate> predicates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        cancellationToken.ThrowIfCancellationRequested();

        TreeModel? tree = null;
        var walked = false;

        foreach (var predicate in predicates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ok = predicate switch
            {
                ExpectForeground => _foreground.GetForegroundWindow() == hwnd,
                ExpectNode node => Matches(EnsureTree(), node),
                ExpectText text => Matches(EnsureTree(), text),
                _ => throw new ArgumentException(
                    $"Unknown expect predicate: {predicate.GetType().Name}.",
                    nameof(predicates)),
            };

            if (!ok)
            {
                return Task.FromResult(ExpectResult.Failed(predicate.FailureName));
            }
        }

        return Task.FromResult(ExpectResult.Success);

        TreeModel? EnsureTree()
        {
            if (!walked)
            {
                tree = _walker.Walk(hwnd, new SnapshotOptions(), cancellationToken);
                walked = true;
            }

            return tree;
        }
    }

    private static bool Matches(TreeModel? tree, ExpectNode predicate)
        => tree is not null
            && AnyNode(
                tree,
                node => SelectorMatches(node, predicate.Role, predicate.Name, predicate.Identifier)
                    && (predicate.Focused is not { } focused || node.Focused == focused));

    private static bool Matches(TreeModel? tree, ExpectText predicate)
        => tree is not null
            && AnyNode(
                tree,
                node => SelectorMatches(node, predicate.Role, predicate.Name, predicate.Identifier)
                    && node.Value is not null
                    && node.Value.Contains(predicate.Contains, StringComparison.OrdinalIgnoreCase));

    private static bool SelectorMatches(
        TreeModel node,
        string? role,
        string? name,
        string? identifier)
        => (role is null || string.Equals(node.Role, role, StringComparison.Ordinal))
            && (name is null || string.Equals(node.Name, name, StringComparison.Ordinal))
            && (identifier is null || string.Equals(node.Identifier, identifier, StringComparison.Ordinal));

    private static bool AnyNode(TreeModel node, Func<TreeModel, bool> predicate)
        => predicate(node) || node.Children.Any(child => AnyNode(child, predicate));
}
