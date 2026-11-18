using KennectComputer.Host.Control;

namespace KennectComputer.Host.Actions;

/// <summary>
/// A mutating action that could not be carried out. The action stops before
/// sending any further input, and <see cref="Code"/> is the protocol error the
/// dispatcher (Task 13) and the ordered batch service (Task 12) report.
///
/// Actions return a success-shaped <see cref="ActionResult"/>, so failures are
/// signalled by this exception rather than by an error field the frozen
/// protocol result does not have. Cancellation is never wrapped: an
/// <see cref="OperationCanceledException"/> propagates unchanged.
/// </summary>
public sealed class ActionFailedException : Exception
{
    public ActionFailedException(
        ErrorCode code,
        string message,
        string? failedPredicate = null)
        : base(message)
    {
        Code = code;
        FailedPredicate = failedPredicate;
    }

    /// <summary>The protocol error code that describes the failure.</summary>
    public ErrorCode Code { get; }

    /// <summary>
    /// The first failing expect predicate's stable name, when the failure was
    /// <c>EXPECTATION_FAILED</c>; otherwise null.
    /// </summary>
    public string? FailedPredicate { get; }
}
