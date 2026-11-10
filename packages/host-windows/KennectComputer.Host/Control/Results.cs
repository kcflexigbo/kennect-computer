using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Control;

/// <summary>
/// Machine-readable failure codes shared with the protocol (DESIGN §9). Values
/// map onto the shared TypeScript <c>ERROR_CODES</c> list.
/// </summary>
public enum ErrorCode
{
    StaleSnapshot,
    WindowGone,
    FocusRefused,
    DesktopUnavailable,
    PermissionMissing,
    HostUnavailable,
    AppNotResponding,
    ExpectationFailed,
    LockedByOtherSession,
    AuthorizationDenied,
    InvalidRequest,
    Unauthorized,
    MethodNotFound,
    ActionTimeout,
    FrameTooLarge,
}

public static class ErrorCodeExtensions
{
    /// <summary>The stable wire code carried in the JSON-RPC error data.</summary>
    public static string ToProtocolCode(this ErrorCode code) => code switch
    {
        ErrorCode.StaleSnapshot => "STALE_SNAPSHOT",
        ErrorCode.WindowGone => "WINDOW_GONE",
        ErrorCode.FocusRefused => "FOCUS_REFUSED",
        ErrorCode.DesktopUnavailable => "DESKTOP_UNAVAILABLE",
        ErrorCode.PermissionMissing => "PERMISSION_MISSING",
        ErrorCode.HostUnavailable => "HOST_UNAVAILABLE",
        ErrorCode.AppNotResponding => "APP_NOT_RESPONDING",
        ErrorCode.ExpectationFailed => "EXPECTATION_FAILED",
        ErrorCode.LockedByOtherSession => "LOCKED_BY_OTHER_SESSION",
        ErrorCode.AuthorizationDenied => "AUTHORIZATION_DENIED",
        ErrorCode.InvalidRequest => "INVALID_REQUEST",
        ErrorCode.Unauthorized => "UNAUTHORIZED",
        ErrorCode.MethodNotFound => "METHOD_NOT_FOUND",
        ErrorCode.ActionTimeout => "ACTION_TIMEOUT",
        ErrorCode.FrameTooLarge => "FRAME_TOO_LARGE",
        _ => "INVALID_REQUEST",
    };
}

/// <summary>A window offered to the caller when a target is ambiguous (R20).</summary>
public sealed class WindowCandidate
{
    public string WindowId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public int Pid { get; init; }
}

/// <summary>Outcome of bringing a window to the foreground.</summary>
public sealed class FocusResult
{
    public bool Ok { get; init; }

    public ErrorCode? Error { get; init; }

    public static FocusResult Success { get; } = new() { Ok = true };

    public static FocusResult Failed(ErrorCode error) => new() { Ok = false, Error = error };
}

/// <summary>
/// Outcome of selecting or focusing a window. On failure <see cref="Error"/> is
/// set; ambiguous process/title targets carry <see cref="Candidates"/> instead
/// of guessing.
/// </summary>
public sealed class SelectResult
{
    public WindowInfo? Window { get; init; }

    public ErrorCode? Error { get; init; }

    public IReadOnlyList<WindowCandidate>? Candidates { get; init; }

    public static SelectResult Success(WindowInfo window) => new() { Window = window };

    public static SelectResult Failed(
        ErrorCode error,
        IReadOnlyList<WindowCandidate>? candidates = null)
        => new() { Error = error, Candidates = candidates };
}
