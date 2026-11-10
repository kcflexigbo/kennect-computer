namespace KennectComputer.Host.Policy;

/// <summary>
/// M1 placeholder policy: every process is authorized for the session.
/// Task 10 replaces this with the real per-app authorization flow.
/// </summary>
public sealed class AllowAllAuthorization : IAppAuthorization
{
    public Task<AuthorizationDecision> AuthorizeAsync(
        string processPath,
        CancellationToken cancellationToken)
        => Task.FromResult(AuthorizationDecision.AllowSession);
}
