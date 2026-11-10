using System.Text.Json;
using System.Text.Json.Nodes;

using KennectComputer.Host.Auth;
using KennectComputer.Host.Control;
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
    private const int ActionErrorCode = -32000;
    private const int UnauthorizedCode = -32001;

    private readonly TokenStore _tokens;
    private readonly IMonitorProvider _monitors;
    private readonly IDesktopState _desktop;
    private readonly WindowEnumerator _windows;
    private readonly SelectionService _selection;

    public JsonRpcDispatcher(
        TokenStore tokens,
        IMonitorProvider monitors,
        IDesktopState desktop,
        WindowEnumerator windows,
        SelectionService selection)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
    }

    public async Task<JsonElement> HandleAsync(JsonElement request)
    {
        try
        {
            return await HandleRequestAsync(request).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Error(null, InternalErrorCode, exception.Message, "INVALID_REQUEST");
        }
    }

    /// <summary>Builds a JSON-RPC parse-error response for malformed JSON.</summary>
    public static JsonElement ParseError(string message)
        => Error(null, ParseErrorCode, message, "INVALID_REQUEST");

    /// <summary>Builds the error returned when a frame header announces more than 16 MiB.</summary>
    public static JsonElement FrameTooLargeError(string message)
        => Error(null, -32002, message, "FRAME_TOO_LARGE");

    /// <summary>Builds an UNAUTHORIZED error, echoed with the request id when one is known.</summary>
    public static JsonElement UnauthorizedError(string message, JsonNode? id = null)
        => Error(id, UnauthorizedCode, message, "UNAUTHORIZED");

    /// <summary>Builds the error returned when the host is already serving its client limit.</summary>
    public static JsonElement HostUnavailableError(string message)
        => Error(null, -32003, message, "HOST_UNAVAILABLE");

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

    private async Task<JsonElement> HandleRequestAsync(JsonElement request)
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
            "windows.list" => HandleWindowsList(id),
            "window.select" => await HandleWindowSelectAsync(id, request).ConfigureAwait(false),
            "action" => await HandleActionAsync(id, request).ConfigureAwait(false),
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
            return UnauthorizedError("The session token is missing, stale, or invalid.", id);
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

    private JsonElement HandleWindowsList(JsonNode? id)
    {
        var result = new WindowsListResult
        {
            Windows = [.. _windows.List()],
        };

        return Success(id, result);
    }

    private async Task<JsonElement> HandleWindowSelectAsync(JsonNode? id, JsonElement request)
    {
        var criteria = new SelectCriteria();

        if (request.TryGetProperty("params", out var parameters))
        {
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                return Error(
                    id,
                    InvalidRequestCode,
                    "window.select params must be an object.",
                    "INVALID_REQUEST");
            }

            if (!TryReadOptionalString(parameters, "windowId", out var windowId)
                || !TryReadOptionalString(parameters, "process", out var process)
                || !TryReadOptionalString(parameters, "title", out var title))
            {
                return Error(
                    id,
                    InvalidRequestCode,
                    "windowId, process, and title must be strings.",
                    "INVALID_REQUEST");
            }

            criteria = new SelectCriteria
            {
                WindowId = windowId,
                Process = process,
                Title = title,
            };
        }

        if (criteria.IsEmpty)
        {
            return Error(
                id,
                InvalidRequestCode,
                "Provide windowId, process, or title to select a window.",
                "INVALID_REQUEST");
        }

        var result = await _selection
            .SelectAsync(criteria, CancellationToken.None)
            .ConfigureAwait(false);

        return FromSelectResult(id, result);
    }

    private async Task<JsonElement> HandleActionAsync(JsonNode? id, JsonElement request)
    {
        if (!request.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            return Error(id, InvalidRequestCode, "action params must be an object.", "INVALID_REQUEST");
        }

        if (!parameters.TryGetProperty("kind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String)
        {
            return Error(id, InvalidRequestCode, "action kind is required.", "INVALID_REQUEST");
        }

        var kind = kindElement.GetString();
        if (!string.Equals(kind, "focus", StringComparison.Ordinal))
        {
            return Error(
                id,
                InvalidRequestCode,
                $"Unsupported action kind: {kind}. Only \"focus\" is available in this milestone.",
                "INVALID_REQUEST");
        }

        if (!parameters.TryGetProperty("windowId", out var windowIdElement)
            || windowIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(windowIdElement.GetString()))
        {
            return Error(
                id,
                InvalidRequestCode,
                "windowId is required for a focus action.",
                "INVALID_REQUEST");
        }

        var result = await _selection
            .SelectAsync(windowIdElement.GetString()!, CancellationToken.None)
            .ConfigureAwait(false);

        return FromSelectResult(id, result);
    }

    private static JsonElement FromSelectResult(JsonNode? id, SelectResult result)
    {
        if (result.Error is { } error)
        {
            return Error(
                id,
                RpcCodeFor(error),
                SelectErrorMessages.Describe(error, result.Candidates?.Count ?? 0),
                error.ToProtocolCode(),
                result.Candidates);
        }

        return Success(id, new WindowResult { Window = result.Window });
    }

    private static bool TryReadOptionalString(
        JsonElement parameters,
        string propertyName,
        out string? value)
    {
        value = null;

        if (!parameters.TryGetProperty(propertyName, out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static int RpcCodeFor(ErrorCode code) => code switch
    {
        ErrorCode.InvalidRequest => InvalidRequestCode,
        ErrorCode.MethodNotFound => MethodNotFoundCode,
        ErrorCode.Unauthorized => UnauthorizedCode,
        ErrorCode.FrameTooLarge => -32002,
        ErrorCode.HostUnavailable => -32003,
        _ => ActionErrorCode,
    };

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

    private static JsonElement Error(
        JsonNode? id,
        int code,
        string message,
        string errorCode,
        IReadOnlyList<WindowCandidate>? candidates = null)
    {
        var data = new JsonObject
        {
            ["code"] = errorCode,
        };

        if (candidates is not null)
        {
            data["candidates"] = JsonSerializer.SerializeToNode(candidates, ProtocolJson.Options);
        }

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["data"] = data,
            },
        };

        return JsonSerializer.SerializeToElement(response, ProtocolJson.Options);
    }

    /// <summary>The <c>{ window }</c> result of <c>window.select</c> and focus actions.</summary>
    private sealed class WindowResult
    {
        public WindowInfo? Window { get; init; }
    }

    private static class SelectErrorMessages
    {
        public static string Describe(ErrorCode error, int candidateCount) => error switch
        {
            ErrorCode.WindowGone => "The window is gone.",
            ErrorCode.FocusRefused =>
                "Windows refused to bring the window to the foreground; no input was sent.",
            ErrorCode.AuthorizationDenied => "The app is not authorized for control.",
            ErrorCode.InvalidRequest when candidateCount > 1 =>
                $"The target is ambiguous: {candidateCount} windows match. "
                + "Select one of the candidate windowIds.",
            ErrorCode.InvalidRequest => "No window is selected.",
            _ => $"The window operation failed ({error.ToProtocolCode()}).",
        };
    }
}
