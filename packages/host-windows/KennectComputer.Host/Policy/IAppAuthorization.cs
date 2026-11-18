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
/// Authorizes controlling a process before the host focuses it. The host uses
/// the config-driven <see cref="AppAuthorization"/>; tests may use
/// <see cref="AllowAllAuthorization"/>.
/// </summary>
public interface IAppAuthorization
{
    Task<AuthorizationDecision> AuthorizeAsync(
        string processPath,
        CancellationToken cancellationToken);
}
