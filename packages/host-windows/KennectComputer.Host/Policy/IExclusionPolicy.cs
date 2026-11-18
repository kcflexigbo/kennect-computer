using KennectComputer.Host.Config;

namespace KennectComputer.Host.Policy;

/// <summary>
/// Decides whether a window is private and must never be listed, selected,
/// snapshotted, or controlled (DESIGN §8).
/// </summary>
public interface IExclusionPolicy
{
    bool IsExcluded(string processName, string title);
}

/// <summary>
/// Implements <c>privacy.excludedProcesses</c> and <c>privacy.excludedTitles</c>.
///
/// Process entries match the process name case-insensitively and ignore a
/// trailing <c>.exe</c> on either side (R34). Title entries are case-insensitive
/// <c>*</c> globs; everything else in a title pattern is literal.
/// </summary>
public sealed class ExclusionPolicy : IExclusionPolicy
{
    private readonly HashSet<string> _processNames;
    private readonly IReadOnlyList<string> _titlePatterns;

    public ExclusionPolicy(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _processNames = new HashSet<string>(
            config.Privacy.ExcludedProcesses
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(NormalizeProcessName),
            StringComparer.OrdinalIgnoreCase);

        _titlePatterns = config.Privacy.ExcludedTitles
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToList();
    }

    public bool IsExcluded(string processName, string title)
    {
        if (!string.IsNullOrWhiteSpace(processName)
            && _processNames.Contains(NormalizeProcessName(processName)))
        {
            return true;
        }

        if (string.IsNullOrEmpty(title))
        {
            return false;
        }

        foreach (var pattern in _titlePatterns)
        {
            if (GlobMatches(title, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeProcessName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    /// <summary>
    /// Case-insensitive <c>*</c> wildcard match with a linear two-pointer scan,
    /// so a hostile pattern cannot cause catastrophic regex backtracking.
    /// </summary>
    internal static bool GlobMatches(string text, string pattern)
    {
        var textIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var retryFrom = 0;

        while (textIndex < text.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '*'
                    || char.ToUpperInvariant(pattern[patternIndex])
                        == char.ToUpperInvariant(text[textIndex])))
            {
                if (pattern[patternIndex] == '*')
                {
                    starIndex = patternIndex;
                    retryFrom = textIndex;
                    patternIndex++;
                }
                else
                {
                    patternIndex++;
                    textIndex++;
                }
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                retryFrom++;
                textIndex = retryFrom;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}
