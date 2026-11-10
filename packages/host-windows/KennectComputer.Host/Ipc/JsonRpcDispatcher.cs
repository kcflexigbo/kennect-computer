using System.Text.Json;
using System.Text.Json.Nodes;

using KennectComputer.Host.Auth;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Ipc;

/// <summary>
/// Handles JSON-RPC requests carried by framed pipe messages.
/// The dispatcher is transport-agnostic so tests can drive it directly.
/// </summary>
public sealed class JsonRpcDispatcher
{
    private const int InvalidRequestCode = -32600;
    private const int MethodNotFoundCode = -32601;
    private const int ParseErrorCode = -32700;
    private const int InternalErrorCode = -32603;
    private const int UnauthorizedCode = -32001;

    private readonly TokenStore _tokens;
    private readonly IMonitorProvider _monitors;
    private readonly IDesktopState _desktop;

    public JsonRpcDispatcher(TokenStore tokens, IMonitorProvider monitors, IDesktopState desktop)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
    }

    public Task<JsonElement> HandleAsync(JsonElement request)
    {
        try
        {
            return Task.FromResult(Handle(request));
        }
        catch (Exception exception)
        {
            return Task.FromResult(Error(null, InternalErrorCode, exception.Message, "INVALID_REQUEST"));
        }
    }

    /// <summary>Builds a JSON-RPC parse-error response for malformed JSON.</summary>
    public static JsonElement ParseError(string message)
        => Error(null, ParseErrorCode, message, "INVALID_REQUEST");

    /// <summary>Builds the error returned when a frame header announces more than 16 MiB.</summary>
    public static JsonElement FrameTooLargeError(string message)
        => Error(null, -32002, message, "FRAME_TOO_LARGE");

    /// <summary>True when a response is an UNAUTHORIZED error, after which the pipe must close.</summary>
    public static bool IsUnauthorized(JsonElement response)
        => response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.String
            && string.Equals(code.GetString(), "UNAUTHORIZED", StringComparison.Ordinal);

    private JsonElement Handle(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
        {
            return Error(null, InvalidRequestCode, "A JSON-RPC request must be an object.", "INVALID_REQUEST");
        }

        var id = request.TryGetProperty("id", out var idElement)
            ? JsonNode.Parse(idElement.GetRawText())
            : null;

        if (!request.TryGetProperty("method", out var methodElement)
            || methodElement.ValueKind != JsonValueKind.String)
        {
            return Error(id, InvalidRequestCode, "The request has no method.", "INVALID_REQUEST");
        }

        return methodElement.GetString() switch
        {
            "hello" => HandleHello(id, request),
            "status" => HandleStatus(id),
            var method => Error(id, MethodNotFoundCode, $"Method not found: {method}", "METHOD_NOT_FOUND"),
        };
    }

    private JsonElement HandleHello(JsonNode? id, JsonElement request)
    {
        if (!request.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("token", out var tokenElement)
            || tokenElement.ValueKind != JsonValueKind.String
            || !_tokens.Validate(tokenElement.GetString()))
        {
            return Error(id, UnauthorizedCode, "The session token is missing, stale, or invalid.", "UNAUTHORIZED");
        }

        if (parameters.TryGetProperty("protocolVersion", out var versionElement)
            && (versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var protocolVersion)
                || protocolVersion != HostInfo.ProtocolVersion))
        {
            return Error(id, InvalidRequestCode, "Unsupported protocol version.", "INVALID_REQUEST");
        }

        var result = new HelloResult
        {
            HostVersion = HostInfo.Version,
            ProtocolVersion = HostInfo.ProtocolVersion,
            Platform = HostInfo.Platform,
            Pid = Environment.ProcessId,
        };

        return Success(id, result);
    }

    private JsonElement HandleStatus(JsonNode? id)
    {
        var result = new HostStatus
        {
            HostVersion = HostInfo.Version,
            ProtocolVersion = HostInfo.ProtocolVersion,
            Platform = HostInfo.Platform,
            Locked = _desktop.IsLocked(),
            Monitors = [.. _monitors.GetMonitors()],
            Permissions = new Permissions
            {
                Accessibility = true,
                ScreenRecording = true,
            },
        };

        return Success(id, result);
    }

    private static JsonElement Success(JsonNode? id, object result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = JsonSerializer.SerializeToNode(result, ProtocolJson.Options),
        };

        return JsonSerializer.SerializeToElement(response, ProtocolJson.Options);
    }

    private static JsonElement Error(JsonNode? id, int code, string message, string errorCode)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["data"] = new JsonObject
                {
                    ["code"] = errorCode,
                },
            },
        };

        return JsonSerializer.SerializeToElement(response, ProtocolJson.Options);
    }
}
