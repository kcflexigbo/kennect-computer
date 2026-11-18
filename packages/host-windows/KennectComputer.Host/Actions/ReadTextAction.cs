using KennectComputer.Host.Control;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Actions;

/// <summary>
/// The host-side form of a shared <c>ReadTextRequest</c> (R9). Task 13 parses
/// the protocol JSON into this record; the node ID is required and is valid
/// only for the snapshot generation that produced it.
/// </summary>
public sealed record ReadTextRequest
{
    /// <summary>The snapshot node whose text is read; valid only for its generation.</summary>
    public string NodeId { get; init; } = string.Empty;

    /// <summary>Maximum characters to return; defaults to 10,000 and is capped at 50,000.</summary>
    public int? MaxLength { get; init; }
}

/// <summary>
/// The host-side form of a shared <c>ReadTextResult</c> (R10). The text is
/// untrusted app content; it is never logged, audited, or put into an error.
/// </summary>
public sealed record ReadTextResult
{
    public WindowInfo Window { get; init; } = new();

    /// <summary>The node the text was read from.</summary>
    public string NodeId { get; init; } = string.Empty;

    /// <summary>The live element's text, capped to the requested bound.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The length of <see cref="Text"/> in UTF-16 code units.</summary>
    public int Length { get; init; }

    /// <summary>True when the source text was longer than the cap.</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Reads a resolved node's text in the selected window (DESIGN §6.9).
///
/// The selected window is re-resolved through the shared preflight resolver,
/// so a missing selection is <c>INVALID_REQUEST</c> and a dead window is
/// <c>WINDOW_GONE</c>. The node ID's generation must still be the stored
/// snapshot's, and the live element must still match its fingerprint;
/// otherwise the result is <c>STALE_SNAPSHOT</c>. TextPattern is preferred and
/// ValuePattern is the fallback; a node with neither is <c>INVALID_REQUEST</c>.
/// The returned text is capped (default 10,000, at most 50,000) and reports
/// <c>truncated</c>. The action is read-only: it never sends input, focuses the
/// window, or evaluates expectations, and no window content appears in errors.
/// </summary>
public sealed class ReadTextAction
{
    public const int DefaultMaxLength = 10_000;
    public const int MaxLengthCap = 50_000;

    private readonly IUiaNodeResolver _resolver;
    private readonly SnapshotStore _snapshots;
    private readonly SelectionStore _selection;
    private readonly WindowRegistry _registry;

    public ReadTextAction(
        IUiaNodeResolver resolver,
        SnapshotStore snapshots,
        SelectionStore selection,
        WindowRegistry registry)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public Task<ReadTextResult> ExecuteAsync(
        ReadTextRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = ActionPreflight.ResolveSelectedWindow(
            _selection,
            _registry,
            "reading text");

        // The shared schema requires a node ID; the host repeats the check so a
        // malformed request can never reach the resolver.
        if (string.IsNullOrWhiteSpace(request.NodeId))
        {
            throw Failure(
                ErrorCode.InvalidRequest,
                "A nodeId is required to read text.");
        }

        // TryGetNode re-checks the node ID's generation against the stored
        // snapshot, so an ID from an older generation never reaches the
        // resolver or the live element.
        if (!_snapshots.TryGetNode(resolved.Info.WindowId, request.NodeId, out var target)
            || target is null)
        {
            throw Failure(
                ErrorCode.StaleSnapshot,
                "The node is from an older snapshot; take a new snapshot before reading its text.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var live = _resolver.Resolve(resolved.Hwnd, target, cancellationToken)
            ?? throw Failure(
                ErrorCode.StaleSnapshot,
                "The node no longer matches the live element; take a new snapshot.");

        // DESIGN §8 hard rule 4: password fields are never read, even when the
        // provider exposes a value.
        if (live.IsPassword)
        {
            throw Failure(
                ErrorCode.InvalidRequest,
                "The node is a password field; its content is never read.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var text = ReadText(live, cancellationToken);

        var maxLength = Math.Clamp(
            request.MaxLength ?? DefaultMaxLength,
            1,
            MaxLengthCap);
        var truncated = text.Length > maxLength;
        if (truncated)
        {
            text = Truncate(text, maxLength);
        }

        return Task.FromResult(new ReadTextResult
        {
            Window = resolved.Info,
            NodeId = request.NodeId,
            Text = text,
            Length = text.Length,
            Truncated = truncated,
        });
    }

    /// <summary>
    /// Reads the element's content, preferring the document-like TextPattern.
    /// A pattern that reports success without text is treated as unavailable so
    /// the edit-like ValuePattern still gets a chance.
    /// </summary>
    private static string ReadText(IUiaNode live, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (live.TryGetText(out var text) && text is not null)
        {
            return text;
        }

        // Each pattern call crosses into the app's UIA provider, so the token
        // is checked again before falling back.
        cancellationToken.ThrowIfCancellationRequested();
        if (live.TryGetValue(out var value) && value is not null)
        {
            return value;
        }

        throw Failure(
            ErrorCode.InvalidRequest,
            "The node exposes no text or value to read; this control's content is unavailable.");
    }

    /// <summary>
    /// Cuts at the code-unit cap without splitting a surrogate pair: returning
    /// half of a pair would not be a valid Unicode string on the wire.
    /// </summary>
    private static string Truncate(string text, int maxLength)
    {
        var length = maxLength;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    private static ActionFailedException Failure(ErrorCode code, string message)
        => new(code, message);
}
