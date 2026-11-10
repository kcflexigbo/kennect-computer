namespace KennectComputer.Host.Policy;

/// <summary>The user's answer to the per-app authorization prompt.</summary>
public enum AuthorizationDecision
{
    /// <summary>Allow this one control operation.</summary>
    AllowOnce,

    /// <summary>Allow this process for the rest of the session.</summary>
    AllowSession,

    /// <summary>Refuse control of the process; no focus or input is sent.</summary>
    Deny,
}

/// <summary>
/// Authorizes controlling a process before the host focuses it. Task 10
/// replaces the allow-all stub with the config-driven per-app policy.
/// </summary>
public interface IAppAuthorization
{
    Task<AuthorizationDecision> AuthorizeAsync(
        string processPath,
        CancellationToken cancellationToken);
}
