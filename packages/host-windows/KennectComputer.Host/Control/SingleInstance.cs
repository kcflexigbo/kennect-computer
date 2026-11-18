namespace KennectComputer.Host.Control;

/// <summary>
/// Ensures one host per user session. A named mutex is the OS-wide lock; a
/// process-local registry handles the mutex's reentrancy, because Windows
/// mutexes can be acquired recursively by the thread that already owns them
/// (the unit test and a second in-process instance must still be rejected).
///
/// A second launch signals the running host to show its tray through a named
/// auto-reset event. Signalling is best effort: when the event cannot be
/// opened the second host still refuses to start rather than blocking.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int TryAcquireTimeoutMs = 0;
    private const string LocalPrefix = @"Local\";

    private static readonly object RegistryGate = new();
    private static readonly HashSet<string> HeldNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _name;
    private readonly string _signalName;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _showTraySignal;
    private readonly bool _ownsMutexOnCreation;
    private bool _acquired;
    private bool _disposed;

    /// <summary>
    /// Creates the named lock. A caller that creates the OS object also owns a
    /// named event the running host waits on to surface its tray.
    /// </summary>
    public SingleInstance(string? name = null)
    {
        _name = string.IsNullOrWhiteSpace(name) ? DefaultName() : name.Trim();
        _mutex = new Mutex(initiallyOwned: true, _name, out var createdNew);
        _ownsMutexOnCreation = createdNew;
        _signalName = _name + ".show-tray";

        // Create-or-open on every instance. When a crashed host left the mutex
        // abandoned, the survivor may not have created the event either; making
        // it unconditional guarantees a running host always has one to wait on.
        // A second instance's handle closes harmlessly when it exits.
        _showTraySignal = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            _signalName);
    }

    /// <summary>
    /// The event a second launch signals so the running host can show its tray.
    /// </summary>
    public EventWaitHandle? ShowTraySignal => _showTraySignal;

    /// <summary>
    /// True when this process becomes the host. False means another host owns
    /// the lock; the existing host has been signalled to show its tray.
    /// </summary>
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_acquired)
        {
            return true;
        }

        // Mutex ownership is per thread, so the same-process case is tracked
        // separately; otherwise a second instance on this thread would recurse.
        lock (RegistryGate)
        {
            if (!HeldNames.Add(_name))
            {
                SignalExistingInstance();
                return false;
            }
        }

        var ownsMutex = _ownsMutexOnCreation;
        if (!ownsMutex)
        {
            try
            {
                ownsMutex = _mutex.WaitOne(TryAcquireTimeoutMs);
            }
            catch (AbandonedMutexException)
            {
                // The previous host died while holding the mutex; the lock is
                // now ours and remains valid.
                ownsMutex = true;
            }
        }

        if (!ownsMutex)
        {
            lock (RegistryGate)
            {
                HeldNames.Remove(_name);
            }

            SignalExistingInstance();
            return false;
        }

        _acquired = true;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_acquired)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not held by this thread (for example, disposal ran on another
                // thread); the process exit releases it.
            }

            lock (RegistryGate)
            {
                HeldNames.Remove(_name);
            }
        }

        _mutex.Dispose();
        _showTraySignal?.Dispose();
    }

    private void SignalExistingInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(_signalName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // No running host created the event; nothing to show, and the
            // second instance must not block waiting for one.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string DefaultName()
        => LocalPrefix + "KennectComputer.Host." + SanitizeUserName(Environment.UserName);

    private static string SanitizeUserName(string userName)
    {
        var sanitized = new string(userName
            .Trim()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray());

        return sanitized.Length == 0 ? "user" : sanitized;
    }
}
