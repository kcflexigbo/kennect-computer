namespace KennectComputer.Host.Actions;

/// <summary>
/// The clock seam for the wait action (DESIGN §6.8). The action reads the
/// current time and sleeps between polls only through this interface, so tests
/// can drive the deadline deterministically instead of sleeping.
/// Cancellation must propagate as <see cref="OperationCanceledException"/>; it
/// is never wrapped.
/// </summary>
public interface IWaitClock
{
    /// <summary>The current UTC time, used to measure the deadline and elapsed time.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Completes after <paramref name="delay"/>, or throws
    /// <see cref="OperationCanceledException"/> when the token is or becomes
    /// cancelled.
    /// </summary>
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>The production clock: wall time and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class SystemWaitClock : IWaitClock
{
    public static SystemWaitClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}
