using KennectComputer.Host.Control;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Actions;

/// <summary>
/// One ordered step in a batch (R9). Task 13 maps the shared flattened
/// discriminated union onto these records; the union itself keeps the request
/// intact so the batch dispatches to the action that owns it.
/// </summary>
public abstract record ActionStep
{
    /// <summary>The shared step kind, for example <c>click</c> or <c>readText</c>.</summary>
    public abstract string Kind { get; }
}

/// <summary>A click step, kind <c>click</c>.</summary>
public sealed record ClickStep : ActionStep
{
    public ClickRequest Request { get; init; } = new();

    public override string Kind => "click";
}

/// <summary>A type step, kind <c>type</c>.</summary>
public sealed record TypeStep : ActionStep
{
    public TypeRequest Request { get; init; } = new();

    public override string Kind => "type";
}

/// <summary>A key step, kind <c>key</c>.</summary>
public sealed record KeyStep : ActionStep
{
    public KeyRequest Request { get; init; } = new();

    public override string Kind => "key";
}

/// <summary>A scroll step, kind <c>scroll</c>.</summary>
public sealed record ScrollStep : ActionStep
{
    public ScrollRequest Request { get; init; } = new();

    public override string Kind => "scroll";
}

/// <summary>A wait step, kind <c>wait</c>.</summary>
public sealed record WaitStep : ActionStep
{
    public WaitRequest Request { get; init; } = new();

    public override string Kind => "wait";
}

/// <summary>A read-text step, kind <c>readText</c>.</summary>
public sealed record ReadTextStep : ActionStep
{
    public ReadTextRequest Request { get; init; } = new();

    public override string Kind => "readText";
}

/// <summary>
/// The host-side form of a shared <c>BatchRequest</c> (R9). Task 13 parses the
/// protocol JSON into this record and rejects an empty list at the schema
/// boundary; the host repeats that guard so a malformed request can never run
/// a no-op batch. Snapshots default to off.
/// </summary>
public sealed record BatchRequest
{
    /// <summary>The ordered, serial steps; at least one is required.</summary>
    public IReadOnlyList<ActionStep> Steps { get; init; } = [];

    /// <summary>Capture an a11y-only snapshot of the selected window first.</summary>
    public bool SnapshotBefore { get; init; }

    /// <summary>Capture an a11y-only snapshot of the selected window last.</summary>
    public bool SnapshotAfter { get; init; }
}

/// <summary>
/// The outcome of one batch step (R10). <see cref="Code"/> carries the host
/// error enum; Task 13 maps it to the shared protocol string with
/// <c>ToProtocolCode()</c>. <see cref="Data"/> is the action's result object
/// (<see cref="ActionResult"/>, <see cref="WaitResult"/>, or
/// <see cref="ReadTextResult"/>) for a successful step and null on failure.
/// </summary>
public sealed record BatchStepResult
{
    /// <summary>The zero-based position of the step in the request.</summary>
    public int Index { get; init; }

    /// <summary>The shared step kind, for example <c>click</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    public bool Ok { get; init; }

    /// <summary>The protocol error code when <see cref="Ok"/> is false.</summary>
    public ErrorCode? Code { get; init; }

    /// <summary>The caller-facing failure message, when the step failed.</summary>
    public string? Message { get; init; }

    /// <summary>The action's result object for a successful step.</summary>
    public object? Data { get; init; }
}

/// <summary>
/// The outcome of an ordered batch (R10). <see cref="FailedIndex"/> is the
/// zero-based step that failed or, for a stop between steps, the step that was
/// not run. Snapshots are accessibility-only and best-effort; a snapshot
/// failure appears in <see cref="Warning"/> without stopping the steps.
/// </summary>
public sealed record BatchResult
{
    /// <summary>The steps that ran, in order, including the failed step.</summary>
    public List<BatchStepResult> Steps { get; init; } = [];

    /// <summary>The zero-based failed or not-run step; null when every step ran.</summary>
    public int? FailedIndex { get; init; }

    public Snapshot? SnapshotBefore { get; init; }

    public Snapshot? SnapshotAfter { get; init; }

    /// <summary>A best-effort snapshot failure, when one occurred.</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// Runs an ordered list of action steps in one host request (DESIGN §6.11).
///
/// Steps run serially on the caller's control token: each step dispatches to
/// the action that owns it, and the first action failure stops the batch
/// without running later steps. The failed step's protocol code and message
/// are recorded with its zero-based index; successful steps carry the action's
/// result object as <see cref="BatchStepResult.Data"/>.
///
/// A stop observed between steps returns the completed steps and names the
/// step that was not run; cancellation inside a step (for example during a
/// wait) propagates as <see cref="OperationCanceledException"/> and is never
/// turned into a step failure or a synthetic code.
///
/// Optional before/after snapshots are accessibility-only (no image) captures
/// of the selected window. The before snapshot is returned only: saving it
/// would allocate a new generation and replace the state the batch's own steps
/// act on, invalidating node IDs and image metadata taken before the batch.
/// The after snapshot is saved, because node IDs from it are the caller's
/// starting point for later actions. Both are best-effort: a failure is
/// recorded in <see cref="BatchResult.Warning"/> and the steps still run.
/// Cancellation during a snapshot propagates unchanged.
/// </summary>
public sealed class BatchAction
{
    private static readonly SnapshotOptions A11yOptions = new()
    {
        Mode = SnapshotMode.A11y,
    };

    private readonly ClickAction _click;
    private readonly TypeAction _type;
    private readonly KeyAction _key;
    private readonly ScrollAction _scroll;
    private readonly WaitAction _wait;
    private readonly ReadTextAction _readText;
    private readonly UiaSnapshotter _snapshotter;
    private readonly SnapshotStore _snapshots;
    private readonly SelectionStore _selection;
    private readonly WindowRegistry _registry;

    public BatchAction(
        ClickAction click,
        TypeAction type,
        KeyAction key,
        ScrollAction scroll,
        WaitAction wait,
        ReadTextAction readText,
        UiaSnapshotter snapshotter,
        SnapshotStore store,
        SelectionStore selection,
        WindowRegistry registry)
    {
        _click = click ?? throw new ArgumentNullException(nameof(click));
        _type = type ?? throw new ArgumentNullException(nameof(type));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _scroll = scroll ?? throw new ArgumentNullException(nameof(scroll));
        _wait = wait ?? throw new ArgumentNullException(nameof(wait));
        _readText = readText ?? throw new ArgumentNullException(nameof(readText));
        _snapshotter = snapshotter ?? throw new ArgumentNullException(nameof(snapshotter));
        _snapshots = store ?? throw new ArgumentNullException(nameof(store));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<BatchResult> ExecuteAsync(
        BatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Max steps is the shared schema's rule (50); the host repeats only the
        // lower bound so an empty list can never reach the action services.
        if (request.Steps is null || request.Steps.Count == 0)
        {
            throw new ActionFailedException(
                ErrorCode.InvalidRequest,
                "A batch requires at least one step.");
        }

        var steps = request.Steps;
        var results = new List<BatchStepResult>(steps.Count);

        // A stop that arrived before anything ran is a cancellation between
        // steps: report the empty prefix and name the first step as not run.
        if (cancellationToken.IsCancellationRequested)
        {
            return new BatchResult { FailedIndex = 0 };
        }

        var (snapshotBefore, beforeWarning) = request.SnapshotBefore
            ? CaptureSnapshot("before", save: false, cancellationToken)
            : (null, null);

        int? failedIndex = null;

        for (var index = 0; index < steps.Count; index++)
        {
            // A stop between steps ends the batch without a fabricated step
            // failure; the step that did not run is the failed index.
            if (cancellationToken.IsCancellationRequested)
            {
                failedIndex = index;
                break;
            }

            var step = steps[index];

            try
            {
                var data = await ExecuteStepAsync(step, cancellationToken)
                    .ConfigureAwait(false);
                results.Add(new BatchStepResult
                {
                    Index = index,
                    Kind = step.Kind,
                    Ok = true,
                    Data = data,
                });
            }
            catch (ActionFailedException failure)
            {
                // Cancellation is deliberately not caught here: an
                // OperationCanceledException must propagate so a stop is never
                // reported as an ordinary step failure.
                results.Add(new BatchStepResult
                {
                    Index = index,
                    Kind = step.Kind,
                    Ok = false,
                    Code = failure.Code,
                    Message = failure.Message,
                });
                failedIndex = index;
                break;
            }
        }

        // A stop also skips the trailing snapshot so the caller gets the
        // completed-steps result promptly; an ordinary step failure still
        // captures the after state when requested.
        var (snapshotAfter, afterWarning) =
            request.SnapshotAfter && !cancellationToken.IsCancellationRequested
                ? CaptureSnapshot("after", save: true, cancellationToken)
                : (null, null);

        return new BatchResult
        {
            Steps = results,
            FailedIndex = failedIndex,
            SnapshotBefore = snapshotBefore,
            SnapshotAfter = snapshotAfter,
            Warning = Combine(beforeWarning, afterWarning),
        };
    }

    /// <summary>
    /// Dispatches one step to the action that owns it. An unknown subtype is a
    /// step-level <c>INVALID_REQUEST</c>; the shared schema cannot produce one,
    /// but the host never guesses at an action for it.
    /// </summary>
    private async Task<object?> ExecuteStepAsync(
        ActionStep step,
        CancellationToken cancellationToken)
    {
        switch (step)
        {
            case ClickStep click:
                return await _click
                    .ExecuteAsync(click.Request, cancellationToken)
                    .ConfigureAwait(false);

            case TypeStep type:
                return await _type
                    .ExecuteAsync(type.Request, cancellationToken)
                    .ConfigureAwait(false);

            case KeyStep key:
                return await _key
                    .ExecuteAsync(key.Request, cancellationToken)
                    .ConfigureAwait(false);

            case ScrollStep scroll:
                return await _scroll
                    .ExecuteAsync(scroll.Request, cancellationToken)
                    .ConfigureAwait(false);

            case WaitStep wait:
                return await _wait
                    .ExecuteAsync(wait.Request, cancellationToken)
                    .ConfigureAwait(false);

            case ReadTextStep readText:
                return await _readText
                    .ExecuteAsync(readText.Request, cancellationToken)
                    .ConfigureAwait(false);

            default:
                throw new ActionFailedException(
                    ErrorCode.InvalidRequest,
                    "The batch step kind is not supported.");
        }
    }

    /// <summary>
    /// Captures an accessibility-only snapshot of the selected window. Only an
    /// after snapshot is saved for later actions; a before snapshot is returned
    /// without touching the store, so it cannot invalidate the state the batch
    /// itself is about to act on. A best-effort failure is returned as a
    /// warning instead of stopping the batch; cancellation is never swallowed.
    /// </summary>
    private (Snapshot? Snapshot, string? Warning) CaptureSnapshot(
        string phase,
        bool save,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = ActionPreflight.ResolveSelectedWindow(
                _selection,
                _registry,
                "taking a batch snapshot");

            cancellationToken.ThrowIfCancellationRequested();
            var capture = _snapshotter.Capture(
                resolved.Hwnd,
                resolved.Info.Bounds,
                A11yOptions,
                cancellationToken);

            // The returned snapshot is self-describing, and a saved snapshot
            // keeps the same bounds the capture used so later coordinate
            // mapping sees one consistent state.
            cancellationToken.ThrowIfCancellationRequested();
            capture.Snapshot.Window = resolved.Info;

            if (save)
            {
                _snapshots.Save(
                    resolved.Info.WindowId,
                    capture.Snapshot,
                    capture.NodeMap,
                    resolved.Info.Bounds);
            }

            return (capture.Snapshot, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Perception degradation is not a step failure: the batch keeps
            // its steps and reports the snapshot problem alongside them.
            return (null, $"The {phase} batch snapshot failed: {exception.Message}");
        }
    }

    private static string? Combine(string? first, string? second)
        => (first, second) switch
        {
            (null, _) => second,
            (_, null) => first,
            _ => first + " " + second,
        };
}
