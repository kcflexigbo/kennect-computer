namespace KennectComputer.Host.Control;

/// <summary>
/// Ownership of the single control session plus the global stop switch.
///
/// Only one client connection may control the desktop at a time (R33): the
/// pipe connection that completes <c>hello</c> owns the session until it
/// disconnects. Acquiring with the owning id is idempotent, so a client may
/// repeat <c>hello</c> on its connection without losing the lock.
///
/// <see cref="Stop"/> aborts the operation that is currently running by
/// cancelling its <see cref="Token"/>, then immediately arms a fresh token so
/// the session itself stays usable (R35). Every host operation that can block
/// runs under <see cref="Token"/>.
/// </summary>
public sealed class ControlSession
{
    private readonly object _gate = new();
    private string? _owner;
    private CancellationTokenSource _operations = new();

    /// <summary>Raised when the session is acquired or released, off the lock.</summary>
    public event EventHandler? StateChanged;

    /// <summary>True while a client owns the control session.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _owner is not null;
            }
        }
    }

    /// <summary>The owning connection id, or null when idle.</summary>
    public string? Owner
    {
        get
        {
            lock (_gate)
            {
                return _owner;
            }
        }
    }

    /// <summary>
    /// The token for the current operation. It is cancelled by <see cref="Stop"/>
    /// (the stop hotkey, the tray Stop item, or the <c>stop</c> host method) and
    /// by <see cref="Release"/> when the owning client disconnects.
    /// </summary>
    public CancellationToken Token
    {
        get
        {
            lock (_gate)
            {
                return _operations.Token;
            }
        }
    }

    /// <summary>
    /// Acquires the session for <paramref name="sessionId"/>. Returns false when
    /// another connection already owns it. Repeating with the owning id
    /// succeeds without changing anything.
    /// </summary>
    public bool TryAcquire(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var changed = false;
        lock (_gate)
        {
            if (_owner is null)
            {
                _owner = sessionId;
                changed = true;
            }
            else if (!string.Equals(_owner, sessionId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    /// <summary>
    /// Releases the session when <paramref name="sessionId"/> owns it. An
    /// in-flight operation belonging to the departing client is cancelled; the
    /// next connection starts from a fresh token.
    /// </summary>
    public void Release(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        CancellationTokenSource? cancelled = null;
        var changed = false;

        lock (_gate)
        {
            if (string.Equals(_owner, sessionId, StringComparison.Ordinal))
            {
                _owner = null;
                cancelled = _operations;
                _operations = new CancellationTokenSource();
                changed = true;
            }
        }

        // Cancel outside the lock: cancellation callbacks must never deadlock
        // against a state read.
        cancelled?.Cancel();

        if (changed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Aborts the current operation and arms a fresh token so the session
    /// continues. Safe to call when nothing is running.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource cancelled;

        lock (_gate)
        {
            cancelled = _operations;
            _operations = new CancellationTokenSource();
        }

        cancelled.Cancel();
    }
}
