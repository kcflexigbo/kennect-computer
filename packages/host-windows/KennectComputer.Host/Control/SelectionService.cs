using System.ComponentModel;
using System.Diagnostics;

using KennectComputer.Host.Policy;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Control;

/// <summary>
/// The target of a <c>window.select</c> call. An explicit ID wins over the
/// process/title criteria (R20).
/// </summary>
public sealed class SelectCriteria
{
    public string? WindowId { get; init; }

    public string? Process { get; init; }

    public string? Title { get; init; }

    public bool IsEmpty
        => string.IsNullOrWhiteSpace(WindowId)
            && string.IsNullOrWhiteSpace(Process)
            && string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// Resolves, authorizes, and focuses a window, then records it as the session
/// target. Resolution is fail-closed: an explicit ID wins, a sole process/title
/// match wins, and an ambiguous match returns the candidates instead of
/// guessing. A window that died clears the stored selection.
/// </summary>
public sealed class SelectionService
{
    private static readonly TimeSpan FocusTimeout = TimeSpan.FromSeconds(1);

    private readonly WindowRegistry _registry;
    private readonly IAppAuthorization _authorization;
    private readonly SelectionStore _selection;
    private readonly ForegroundService _foreground;
    private readonly WindowEnumerator? _windows;
    private readonly Func<int, string?> _processPathResolver;

    public SelectionService(
        WindowRegistry registry,
        IAppAuthorization authorization,
        SelectionStore selection,
        ForegroundService? foreground = null,
        WindowEnumerator? windows = null,
        Func<int, string?>? processPathResolver = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _foreground = foreground ?? new ForegroundService(new WindowsForegroundApi());
        _windows = windows;
        _processPathResolver = processPathResolver ?? TryResolveProcessPath;
    }

    /// <summary>Selects and focuses an explicit, already-issued window ID.</summary>
    public async Task<SelectResult> SelectAsync(
        string windowId,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryResolve(windowId, out var resolved) || resolved is null)
        {
            ClearIfSelected(windowId);
            return SelectResult.Failed(ErrorCode.WindowGone);
        }

        return await SelectResolvedAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a target fail-closed (R20) and selects it: an explicit window ID
    /// wins; otherwise exactly one process/title match must exist.
    /// </summary>
    public async Task<SelectResult> SelectAsync(
        SelectCriteria criteria,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (!string.IsNullOrWhiteSpace(criteria.WindowId))
        {
            return await SelectAsync(criteria.WindowId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(criteria.Process)
            && string.IsNullOrWhiteSpace(criteria.Title))
        {
            return SelectResult.Failed(ErrorCode.InvalidRequest);
        }

        if (_windows is null)
        {
            throw new InvalidOperationException(
                "Selecting by process or title requires a window enumerator.");
        }

        var matches = _windows.List()
            .Where(window => Matches(window, criteria))
            .ToList();

        if (matches.Count == 0)
        {
            return SelectResult.Failed(ErrorCode.WindowGone);
        }

        if (matches.Count == 1)
        {
            return await SelectAsync(matches[0].WindowId, cancellationToken)
                .ConfigureAwait(false);
        }

        return SelectResult.Failed(
            ErrorCode.InvalidRequest,
            [.. matches.Select(window => new WindowCandidate
            {
                WindowId = window.WindowId,
                Title = window.Title,
                Pid = window.Pid,
            })]);
    }

    /// <summary>Re-focuses the recorded selection, clearing it if the window died.</summary>
    public async Task<SelectResult> FocusSelectedAsync(CancellationToken cancellationToken)
    {
        var selected = _selection.Selected;
        if (selected is null)
        {
            return SelectResult.Failed(ErrorCode.InvalidRequest);
        }

        if (!_registry.TryResolve(selected.WindowId, out var resolved) || resolved is null)
        {
            _selection.Clear();
            return SelectResult.Failed(ErrorCode.WindowGone);
        }

        return await SelectResolvedAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SelectResult> SelectResolvedAsync(
        ResolvedWindow resolved,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // R27: authorization is per executable path. A bare process name is not
        // an identity — same-named binaries at other paths, or replacement
        // attacks, would be indistinguishable — so fail closed and never focus.
        var processPath = _processPathResolver(resolved.Pid);
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return SelectResult.Failed(
                ErrorCode.AuthorizationDenied,
                message: $"The executable path for PID {resolved.Pid} "
                    + $"({resolved.ProcessName}) could not be resolved, "
                    + "so the app cannot be authorized.");
        }

        var decision = await _authorization
            .AuthorizeAsync(processPath, cancellationToken)
            .ConfigureAwait(false);

        if (decision == AuthorizationDecision.Deny)
        {
            return SelectResult.Failed(ErrorCode.AuthorizationDenied);
        }

        // Authorization may wait on a user prompt, so re-read the native identity
        // immediately before focusing: a recycled handle must never be focused.
        if (!_registry.TryResolve(resolved.Info.WindowId, out var current) || current is null)
        {
            ClearIfSelected(resolved.Info.WindowId);
            return SelectResult.Failed(ErrorCode.WindowGone);
        }

        var focus = await _foreground
            .FocusAsync(current.Hwnd, FocusTimeout)
            .ConfigureAwait(false);

        if (!focus.Ok)
        {
            return SelectResult.Failed(focus.Error ?? ErrorCode.FocusRefused);
        }

        var window = current.Info;
        window.Focused = true;
        _selection.Select(window);
        return SelectResult.Success(window);
    }

    private void ClearIfSelected(string windowId)
    {
        var selected = _selection.Selected;
        if (selected is not null
            && string.Equals(selected.WindowId, windowId, StringComparison.Ordinal))
        {
            _selection.Clear();
        }
    }

    private static bool Matches(WindowInfo window, SelectCriteria criteria)
    {
        if (!string.IsNullOrWhiteSpace(criteria.Process))
        {
            var name = Path.GetFileName(criteria.Process.Trim());
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            if (!string.Equals(window.ProcessName, name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.Title)
            && !window.Title.Contains(
                criteria.Title.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// The executable path handed to <see cref="IAppAuthorization"/>, or null
    /// when the image path is inaccessible (elevated or protected processes) or
    /// not fully qualified. The process name is never substituted for a path
    /// (R27).
    /// </summary>
    internal static string? TryResolveProcessPath(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var path = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                ? path
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
