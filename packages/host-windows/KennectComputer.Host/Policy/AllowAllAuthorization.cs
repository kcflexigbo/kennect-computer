namespace KennectComputer.Host.Policy;

/// <summary>
/// Test-only policy: every process is authorized for the session. The running
/// host uses the config-driven <see cref="AppAuthorization"/> instead.
/// </summary>
public sealed class AllowAllAuthorization : IAppAuthorization
{
    public Task<AuthorizationDecision> AuthorizeAsync(
        string processPath,
        CancellationToken cancellationToken)
        => Task.FromResult(AuthorizationDecision.AllowSession);
}
