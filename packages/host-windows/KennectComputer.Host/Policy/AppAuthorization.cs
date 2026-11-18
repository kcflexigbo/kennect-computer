using KennectComputer.Host.Config;

namespace KennectComputer.Host.Policy;

/// <summary>
/// The config-driven per-app policy (DESIGN §6, §13).
///
/// An app listed with <c>preAuthorized: true</c> is allowed for the session
/// without prompting; the config key is the process file name without
/// <c>.exe</c> and matches case-insensitively. Otherwise the user is prompted.
/// <see cref="AuthorizationDecision.Deny"/> and
/// <see cref="AuthorizationDecision.AllowSession"/> are remembered per full
/// process path for the session; <see cref="AuthorizationDecision.AllowOnce"/>
/// is not. An empty or unresolvable path is denied without prompting (R27).
/// </summary>
public sealed class AppAuthorization : IAppAuthorization
{
    private readonly IAppConfig _config;
    private readonly IAuthorizationPrompt _prompt;
    private readonly object _gate = new();
    private readonly Dictionary<string, AuthorizationDecision> _decisions =
        new(StringComparer.OrdinalIgnoreCase);

    public AppAuthorization(IAppConfig config, IAuthorizationPrompt prompt)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
    }

    public async Task<AuthorizationDecision> AuthorizeAsync(
        string processPath,
        CancellationToken cancellationToken)
    {
        // R27: a bare process name is not an identity. SelectionService already
        // fails closed when the path cannot be resolved; this guard keeps the
        // policy safe for any caller.
        if (string.IsNullOrWhiteSpace(processPath)
            || !Path.IsPathFullyQualified(processPath))
        {
            return AuthorizationDecision.Deny;
        }

        if (IsPreAuthorized(processPath))
        {
            Cache(processPath, AuthorizationDecision.AllowSession);
            return AuthorizationDecision.AllowSession;
        }

        lock (_gate)
        {
            if (_decisions.TryGetValue(processPath, out var cached))
            {
                return cached;
            }
        }

        AuthorizationDecision decision;
        try
        {
            decision = await _prompt
                .AskAsync(processPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A broken or unavailable prompt must fail closed.
            decision = AuthorizationDecision.Deny;
        }

        if (decision != AuthorizationDecision.AllowOnce)
        {
            Cache(processPath, decision);
        }

        return decision;
    }

    private bool IsPreAuthorized(string processPath)
    {
        var processName = Path.GetFileNameWithoutExtension(processPath);

        foreach (var (key, policy) in _config.Apps)
        {
            if (policy is not { PreAuthorized: true })
            {
                continue;
            }

            var configuredName = Path.GetFileNameWithoutExtension(key.Trim());
            if (string.Equals(configuredName, processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void Cache(string processPath, AuthorizationDecision decision)
    {
        lock (_gate)
        {
            _decisions[processPath] = decision;
        }
    }
}
