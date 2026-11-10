using System.Text.Json;
using System.Text.Json.Serialization;

namespace KennectComputer.Host.Protocol;

/// <summary>Identity of this host build, reported by <c>hello</c> and <c>status</c>.</summary>
public static class HostInfo
{
    public const string Version = "0.1.0";
    public const int ProtocolVersion = 1;
    public const string Platform = "windows";
}

/// <summary>Serialization policy shared by every DTO that crosses the pipe.</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>A rectangle in physical screen pixels.</summary>
public sealed class Rect
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

/// <summary>A display known to the host.</summary>
public sealed class MonitorInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Rect Bounds { get; set; } = new();

    public double Scale { get; set; } = 1.0;

    public bool Primary { get; set; }
}

/// <summary>OS-level permissions the host needs in order to capture and control the desktop.</summary>
public sealed class Permissions
{
    public bool Accessibility { get; set; }

    public bool ScreenRecording { get; set; }
}

/// <summary>Host health and environment, returned by <c>status</c>.</summary>
public sealed class HostStatus
{
    public string HostVersion { get; set; } = HostInfo.Version;

    public int ProtocolVersion { get; set; } = HostInfo.ProtocolVersion;

    public string Platform { get; set; } = HostInfo.Platform;

    public bool Locked { get; set; }

    public List<MonitorInfo> Monitors { get; set; } = [];

    public Permissions Permissions { get; set; } = new();
}

/// <summary>Result of a successful <c>hello</c>.</summary>
public sealed class HelloResult
{
    public string HostVersion { get; set; } = HostInfo.Version;

    public int ProtocolVersion { get; set; } = HostInfo.ProtocolVersion;

    public string Platform { get; set; } = HostInfo.Platform;

    public int Pid { get; set; }
}

/// <summary>Contents of <c>session.json</c>, written next to <c>session.token</c>.</summary>
public sealed class SessionInfo
{
    public string PipeName { get; set; } = string.Empty;

    public int Pid { get; set; }

    public int ProtocolVersion { get; set; }

    public DateTimeOffset StartedAt { get; set; }
}
