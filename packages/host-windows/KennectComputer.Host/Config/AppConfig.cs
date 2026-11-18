namespace KennectComputer.Host.Config;

/// <summary>The config surface the per-app authorization policy reads.</summary>
public interface IAppConfig
{
    /// <summary>Per-app policy keyed by process name without <c>.exe</c>, case-insensitive.</summary>
    Dictionary<string, AppPolicy> Apps { get; }
}

/// <summary>
/// The host configuration, mirroring DESIGN §13 and <c>config/default.json</c>.
/// Every object property is initialized and null-coalesced so a partial user
/// config can never leave the host with a missing section.
/// </summary>
public sealed class AppConfig : IAppConfig
{
    private HostConfig _host = new();
    private ControlConfig _control = new();
    private PerceptionConfig _perception = new();
    private PrivacyConfig _privacy = new();
    private Dictionary<string, AppPolicy> _apps = new(StringComparer.OrdinalIgnoreCase);

    public HostConfig Host
    {
        get => _host;
        set => _host = value ?? new HostConfig();
    }

    public ControlConfig Control
    {
        get => _control;
        set => _control = value ?? new ControlConfig();
    }

    public PerceptionConfig Perception
    {
        get => _perception;
        set => _perception = value ?? new PerceptionConfig();
    }

    public PrivacyConfig Privacy
    {
        get => _privacy;
        set => _privacy = value ?? new PrivacyConfig();
    }

    /// <summary>
    /// Per-app policy. The dictionary is always case-insensitive so
    /// <c>"Feishu"</c> and <c>"feishu.exe"</c> address the same app.
    /// </summary>
    public Dictionary<string, AppPolicy> Apps
    {
        get => _apps;
        set => _apps = value is null
            ? new Dictionary<string, AppPolicy>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, AppPolicy>(value, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class HostConfig
{
    /// <summary>Start the host lazily when the MCP server first needs it.</summary>
    public bool StartOnDemand { get; set; } = true;
}

public sealed class ControlConfig
{
    private StopHotkeyConfig _stopHotkey = new();

    public StopHotkeyConfig StopHotkey
    {
        get => _stopHotkey;
        set => _stopHotkey = value ?? new StopHotkeyConfig();
    }

    /// <summary>Authorization mode; M1 implements <c>per-app</c>.</summary>
    public string AuthorizationMode { get; set; } = "per-app";

    /// <summary>Consequential-action gate; arrives with M2. Defaults to off.</summary>
    public string ConsequentialGate { get; set; } = "off";

    public int ConsequentialCountdownMs { get; set; } = 3000;
}

public sealed class StopHotkeyConfig
{
    public string Windows { get; set; } = "Ctrl+Alt+Shift+Esc";

    public string Macos { get; set; } = "Ctrl+Alt+Shift+Esc";
}

public sealed class PerceptionConfig
{
    public string DefaultMode { get; set; } = "both";

    public int MaxDepth { get; set; } = 12;

    public int MaxNodes { get; set; } = 1500;

    public int ImageLongEdge { get; set; } = 1568;

    public int MaxImageLongEdge { get; set; } = 2576;
}

public sealed class PrivacyConfig
{
    private List<string> _excludedProcesses =
    [
        "KeePass.exe",
        "1Password.exe",
        "Bitwarden.exe",
        "WindowsSecurity.exe",
        "LogonUI.exe",
        "Keychain Access",
        "SecurityAgent",
    ];

    private List<string> _excludedTitles = ["*password*", "*bank*"];

    public List<string> ExcludedProcesses
    {
        get => _excludedProcesses;
        set => _excludedProcesses = value ?? [];
    }

    public List<string> ExcludedTitles
    {
        get => _excludedTitles;
        set => _excludedTitles = value ?? [];
    }

    public int AuditRetentionDays { get; set; } = 7;
}

/// <summary>Per-app policy from the <c>apps</c> section.</summary>
public sealed class AppPolicy
{
    public string ConsequentialGate { get; set; } = "off";

    /// <summary>Skip the first-use prompt for this app (DESIGN §13).</summary>
    public bool PreAuthorized { get; set; }
}
