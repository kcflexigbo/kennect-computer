using KennectComputer.Host.Control;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Actions;

/// <summary>
/// A host-side wait condition (DESIGN §6.8). Task 13 maps the shared TypeScript
/// union onto these records; the union itself is not represented here.
/// A condition describes something to observe: a window, a node, or a node's
/// captured text/value. <see cref="WaitRequest.Present"/> decides whether the
/// condition or its absence is the success state.
/// </summary>
public abstract record WaitCondition;

/// <summary>
/// A top-level window matching a process and/or title. The process is compared
/// case-insensitively and tolerates the <c>.exe</c> suffix on either side; the
/// title is a case-insensitive substring. Omitted fields are wildcards.
/// </summary>
public sealed record WaitWindowCondition : WaitCondition
{
    public string? Process { get; init; }

    public string? Title { get; init; }
}

/// <summary>
/// A node matching the supplied selector. Supplied fields are matched exactly;
/// omitted fields are wildcards. At least one of role, name, or identifier is
/// required.
/// </summary>
public sealed record WaitNodeCondition : WaitCondition
{
    public string? Role { get; init; }

    public string? Name { get; init; }

    public string? Identifier { get; init; }
}

/// <summary>
/// A node whose captured text/value contains <see cref="Contains"/> (ordinal,
/// case-insensitive). The optional selector narrows which nodes are considered;
/// the accessible name alone never satisfies a text condition.
/// </summary>
public sealed record WaitTextCondition : WaitCondition
{
    public string? Role { get; init; }

    public string? Name { get; init; }

    public string? Identifier { get; init; }

    public string Contains { get; init; } = string.Empty;
}

/// <summary>
/// The host-side form of a shared <c>WaitRequest</c> (R9). Task 13 parses the
/// protocol JSON into this record. <see cref="Present"/> defaults to true;
/// the timeout defaults to 10,000 ms and the poll to 100 ms.
/// </summary>
public sealed record WaitRequest
{
    public WaitCondition? Condition { get; init; }

    /// <summary>True to wait for the condition, false to wait for its absence.</summary>
    public bool Present { get; init; } = true;

    /// <summary>
    /// Deadline in milliseconds; defaults to 10,000 and must be in 1..60,000.
    /// </summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Poll interval in milliseconds; clamped to 10..1,000.</summary>
    public int? PollMs { get; init; }
}

/// <summary>
/// The host-side form of a shared <c>WaitResult</c> (R10). <see cref="Window"/>
/// is set only when a window condition matched; node and text conditions leave
/// it null even though they read the selected window. <see cref="ElapsedMs"/>
/// is measured on the injected clock.
/// </summary>
public sealed record WaitResult
{
    public WindowInfo? Window { get; init; }

    public WaitCondition Condition { get; init; } = null!;

    public long ElapsedMs { get; init; }
}

/// <summary>
/// Polls the desktop until a window, node, or node text appears or disappears
/// (DESIGN §6.8).
///
/// A window condition enumerates the top-level windows and needs no selection;
/// it returns the first matching <see cref="WindowInfo"/>. Node and text
/// conditions walk the selected window through the shared preflight resolver,
/// so a missing selection is <c>INVALID_REQUEST</c> and a dead window is
/// <c>WINDOW_GONE</c>. Node selectors are matched exactly, while text is an
/// ordinal case-insensitive substring over the host-internal captured value,
/// never over the accessible name.
///
/// The loop checks cancellation before each observation and each delay.
/// Reaching the deadline is <c>ACTION_TIMEOUT</c> with a message naming the
/// condition; cancellation propagates as <see cref="OperationCanceledException"/>
/// and is never wrapped. The action is read-only: it never focuses a window,
/// sends input, or evaluates expectations.
/// </summary>
public sealed class WaitAction
{
    public const int DefaultTimeoutMs = 10_000;
    public const int MaxTimeoutMs = 60_000;
    public const int DefaultPollMs = 100;
    public const int MinPollMs = 10;
    public const int MaxPollMs = 1_000;

    private readonly IUiaWalker _walker;
    private readonly WindowEnumerator _windows;
    private readonly SelectionStore _selection;
    private readonly WindowRegistry _registry;
    private readonly IWaitClock _clock;

    public WaitAction(
        IUiaWalker walker,
        WindowEnumerator windows,
        SelectionStore selection,
        WindowRegistry registry,
        IWaitClock clock)
    {
        _walker = walker ?? throw new ArgumentNullException(nameof(walker));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<WaitResult> ExecuteAsync(
        WaitRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var condition = request.Condition
            ?? throw Failure(ErrorCode.InvalidRequest, "A wait condition is required.");
        Validate(condition);

        var timeoutMs = request.TimeoutMs ?? DefaultTimeoutMs;
        if (timeoutMs <= 0 || timeoutMs > MaxTimeoutMs)
        {
            throw Failure(
                ErrorCode.InvalidRequest,
                $"A wait timeout must be between 1 and {MaxTimeoutMs} ms.");
        }

        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        var poll = TimeSpan.FromMilliseconds(
            Math.Clamp(request.PollMs ?? DefaultPollMs, MinPollMs, MaxPollMs));
        var started = _clock.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The deadline is checked before observing, so the host never
            // starts a poll at or after the caller's deadline.
            if (_clock.UtcNow - started >= timeout)
            {
                throw Timeout(condition, request.Present, timeoutMs);
            }

            var observed = Observe(condition, cancellationToken);
            if (observed.Observed == request.Present)
            {
                return new WaitResult
                {
                    Window = observed.Window,
                    Condition = condition,
                    ElapsedMs = (long)(_clock.UtcNow - started).TotalMilliseconds,
                };
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Never sleep past the deadline: the last delay is shortened to
            // the remaining time so the timeout error is timely.
            var remaining = timeout - (_clock.UtcNow - started);
            if (remaining <= TimeSpan.Zero)
            {
                throw Timeout(condition, request.Present, timeoutMs);
            }

            await _clock
                .Delay(poll < remaining ? poll : remaining, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Observation Observe(WaitCondition condition, CancellationToken cancellationToken)
    {
        switch (condition)
        {
            case WaitWindowCondition window:
            {
                // Window conditions watch the whole desktop and need no
                // selected target; the first match in enumeration order wins.
                var match = _windows
                    .List()
                    .FirstOrDefault(candidate => WindowMatches(candidate, window));
                return new Observation(match is not null, match);
            }

            case WaitNodeCondition node:
            {
                var tree = WalkSelected("waiting for a node", cancellationToken);
                return new Observation(
                    tree is not null
                        && AnyNode(
                            tree,
                            candidate => SelectorMatches(
                                candidate,
                                node.Role,
                                node.Name,
                                node.Identifier)),
                    Window: null);
            }

            case WaitTextCondition text:
            {
                var tree = WalkSelected("waiting for text", cancellationToken);
                return new Observation(
                    tree is not null
                        && AnyNode(
                            tree,
                            candidate => SelectorMatches(
                                candidate,
                                text.Role,
                                text.Name,
                                text.Identifier)
                                && !candidate.IsPassword
                                && candidate.Value is not null
                                && candidate.Value.Contains(
                                    text.Contains,
                                    StringComparison.OrdinalIgnoreCase)),
                    Window: null);
            }

            default:
                throw Failure(
                    ErrorCode.InvalidRequest,
                    "The wait condition is not supported.");
        }
    }

    private TreeModel? WalkSelected(string pendingAction, CancellationToken cancellationToken)
    {
        var resolved = ActionPreflight.ResolveSelectedWindow(
            _selection,
            _registry,
            pendingAction);

        cancellationToken.ThrowIfCancellationRequested();

        // The same default read budget as expect: a wait is a repeated read,
        // so it stays lighter than a find's one-shot search budget.
        return _walker.Walk(
            resolved.Hwnd,
            new SnapshotOptions { Mode = SnapshotMode.A11y },
            cancellationToken);
    }

    private static void Validate(WaitCondition condition)
    {
        switch (condition)
        {
            case WaitWindowCondition window:
                if (string.IsNullOrWhiteSpace(window.Process)
                    && string.IsNullOrWhiteSpace(window.Title))
                {
                    throw Failure(
                        ErrorCode.InvalidRequest,
                        "A window wait condition requires a process or a title.");
                }

                break;

            case WaitNodeCondition node:
                if (string.IsNullOrWhiteSpace(node.Role)
                    && string.IsNullOrWhiteSpace(node.Name)
                    && string.IsNullOrWhiteSpace(node.Identifier))
                {
                    throw Failure(
                        ErrorCode.InvalidRequest,
                        "A node wait condition requires a role, name, or identifier.");
                }

                break;

            case WaitTextCondition text:
                if (string.IsNullOrWhiteSpace(text.Contains))
                {
                    throw Failure(
                        ErrorCode.InvalidRequest,
                        "A text wait condition requires text to look for.");
                }

                break;

            default:
                throw Failure(
                    ErrorCode.InvalidRequest,
                    "The wait condition is not supported.");
        }
    }

    private static bool WindowMatches(WindowInfo window, WaitWindowCondition condition)
    {
        if (Normalize(condition.Process) is { } process
            && !ProcessMatches(window.ProcessName, process))
        {
            return false;
        }

        return Normalize(condition.Title) is not { } title
            || window.Title.Contains(title, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares executable base names so <c>Feishu.exe</c> and <c>Feishu</c>
    /// describe the same app on either side.
    /// </summary>
    private static bool ProcessMatches(string processName, string process)
        => string.Equals(StripExe(processName), StripExe(process), StringComparison.OrdinalIgnoreCase);

    private static string StripExe(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool SelectorMatches(
        TreeModel node,
        string? role,
        string? name,
        string? identifier)
        => (Normalize(role) is not { } wantedRole
                || string.Equals(node.Role, wantedRole, StringComparison.Ordinal))
            && (Normalize(name) is not { } wantedName
                || string.Equals(node.Name, wantedName, StringComparison.Ordinal))
            && (Normalize(identifier) is not { } wantedIdentifier
                || string.Equals(node.Identifier, wantedIdentifier, StringComparison.Ordinal));

    private static bool AnyNode(TreeModel node, Func<TreeModel, bool> matches)
        => matches(node) || node.Children.Any(child => AnyNode(child, matches));

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ActionFailedException Timeout(
        WaitCondition condition,
        bool present,
        int timeoutMs)
        => Failure(
            ErrorCode.ActionTimeout,
            $"Timed out after {timeoutMs} ms waiting for {Describe(condition)} to "
                + (present ? "appear." : "disappear."));

    /// <summary>
    /// Names the condition in a timeout message without including any app
    /// content: only the caller-supplied selector values appear.
    /// </summary>
    private static string Describe(WaitCondition condition)
    {
        switch (condition)
        {
            case WaitWindowCondition window:
            {
                var parts = new List<string>();
                if (Normalize(window.Process) is { } process)
                {
                    parts.Add($"process \"{process}\"");
                }

                if (Normalize(window.Title) is { } title)
                {
                    parts.Add($"title containing \"{title}\"");
                }

                return $"a window with {string.Join(" and ", parts)}";
            }

            case WaitNodeCondition node:
                return $"a node with {DescribeSelector(node.Role, node.Name, node.Identifier)}";

            case WaitTextCondition text:
            {
                var selector = DescribeSelector(text.Role, text.Name, text.Identifier);
                var narrowed = selector.Length == 0 ? string.Empty : $" with {selector}";
                return $"text containing \"{text.Contains}\"{narrowed}";
            }

            default:
                return "the condition";
        }
    }

    private static string DescribeSelector(string? role, string? name, string? identifier)
    {
        var parts = new List<string>();
        if (Normalize(role) is { } wantedRole)
        {
            parts.Add($"role \"{wantedRole}\"");
        }

        if (Normalize(name) is { } wantedName)
        {
            parts.Add($"name \"{wantedName}\"");
        }

        if (Normalize(identifier) is { } wantedIdentifier)
        {
            parts.Add($"identifier \"{wantedIdentifier}\"");
        }

        return string.Join(" and ", parts);
    }

    private static ActionFailedException Failure(ErrorCode code, string message)
        => new(code, message);

    /// <summary>Whether the condition is present, plus the window that proved it.</summary>
    private readonly record struct Observation(bool Observed, WindowInfo? Window);
}
