namespace KennectComputer.Host.Tests;

/// <summary>
/// A fact that runs only when the operator explicitly opts into tests that
/// launch or control real desktop apps. The normal suite must never open,
/// focus, or kill the user's applications; those tests are skipped by default.
/// </summary>
public sealed class LiveDesktopFactAttribute : FactAttribute
{
    public const string OptInVariable = "KENNECT_LIVE_DESKTOP_TESTS";

    public LiveDesktopFactAttribute()
    {
        if (!IsEnabled())
        {
            Skip = $"Set {OptInVariable}=1 to run tests that touch real desktop apps.";
        }
    }

    public static bool IsEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable(OptInVariable),
            "1",
            StringComparison.Ordinal);
}
