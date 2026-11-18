using System.Text.Json;
using System.Text.Json.Nodes;

using KennectComputer.Host.Actions;
using KennectComputer.Host.Auth;
using KennectComputer.Host.Config;
using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
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

    /// <summary>Upper bounds that keep a malformed request from exhausting the host.</summary>
    private const int MaxSnapshotDepth = 100;
    private const int MaxSnapshotNodes = 50_000;

    // Click counts become a mouse-event array and type text becomes input
    // events, so both carry the shared schema's documented maxima. A hidden
    // client that bypasses the schema must not reach an unbounded allocation.
    private const int MaxClickCount = 10;
    private const int MaxSelectorTextLength = 1000;
    private const int MaxTypeTextLength = 100_000;

    /// <summary>A crop rectangle in the previous snapshot image's pixel space.</summary>
    private readonly record struct SnapshotRegion(int X, int Y, int Width, int Height);

    private readonly TokenStore _tokens;
    private readonly IMonitorProvider _monitors;
    private readonly IDesktopState _desktop;
    private readonly WindowEnumerator _windows;
    private readonly SelectionService _selection;
    private readonly WindowRegistry _registry;
    private readonly UiaSnapshotter _snapshotter;
    private readonly SnapshotStore _snapshots;
    private readonly ControlSession _control;
    private readonly ClickAction _click;
    private readonly TypeAction _type;
    private readonly KeyAction _key;
    private readonly ScrollAction _scroll;
    private readonly WaitAction _wait;
    private readonly ReadTextAction _readText;
    private readonly FindAction _find;
    private readonly BatchAction _batch;
    private readonly int _defaultMaxDepth;
    private readonly int _defaultMaxNodes;
    private readonly int _defaultImageLongEdge;
    private readonly int _maxImageLongEdge;
    private readonly SnapshotMode _defaultMode;

    public JsonRpcDispatcher(
        TokenStore tokens,
        IMonitorProvider monitors,
        IDesktopState desktop,
        WindowEnumerator windows,
        SelectionService selection,
        WindowRegistry registry,
        UiaSnapshotter snapshotter,
        SnapshotStore snapshots,
        ControlSession control,
        ClickAction click,
        TypeAction type,
        KeyAction key,
        ScrollAction scroll,
        WaitAction wait,
        ReadTextAction readText,
        FindAction find,
        BatchAction batch,
        PerceptionConfig? perception = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _snapshotter = snapshotter ?? throw new ArgumentNullException(nameof(snapshotter));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _click = click ?? throw new ArgumentNullException(nameof(click));
        _type = type ?? throw new ArgumentNullException(nameof(type));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _scroll = scroll ?? throw new ArgumentNullException(nameof(scroll));
        _wait = wait ?? throw new ArgumentNullException(nameof(wait));
        _readText = readText ?? throw new ArgumentNullException(nameof(readText));
        _find = find ?? throw new ArgumentNullException(nameof(find));
        _batch = batch ?? throw new ArgumentNullException(nameof(batch));

        // Perception settings are host defaults: a request may tighten them,
        // but a per-user config change must change behavior. The protocol's
        // hard caps still bound the effective values.
        var configured = perception ?? new PerceptionConfig();
        _defaultMaxDepth = Math.Clamp(configured.MaxDepth, 1, MaxSnapshotDepth);
        _defaultMaxNodes = Math.Clamp(configured.MaxNodes, 1, MaxSnapshotNodes);
        _maxImageLongEdge = Math.Clamp(
            configured.MaxImageLongEdge,
            1,
            SnapshotOptions.DefaultMaxImageLongEdge);
        _defaultImageLongEdge = Math.Clamp(
            configured.ImageLongEdge <= 0
                ? SnapshotOptions.DefaultImageLongEdge
                : configured.ImageLongEdge,
            1,
            _maxImageLongEdge);
        _defaultMode = TryParseSnapshotMode(configured.DefaultMode, out var mode)
            ? mode
            : SnapshotMode.Both;
    }

    /// <summary>
    /// Handles a request outside a pipe connection. Direct callers share one
    /// local session id, which is enough for unit tests and embedding.
    /// </summary>
    public Task<JsonElement> HandleAsync(JsonElement request)
        => HandleAsync(request, "local");

    /// <summary>
    /// Handles a request for a pipe connection. <paramref name="sessionId"/> is
    /// the connection id used for control-session ownership (R33).
    /// </summary>
    public async Task<JsonElement> HandleAsync(JsonElement request, string sessionId)
    {
        try
        {
            return await HandleRequestAsync(request, sessionId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop cancels the in-flight operation; the client sees an abort
            // rather than an internal error, and the session stays alive.
            return Error(
                ReadRequestId(request),
                ActionErrorCode,
                "The operation was stopped.",
                "ACTION_TIMEOUT");
        }
        catch (ActionFailedException failure)
        {
            // Actions signal failures with their protocol code; the failed
            // expect predicate (when any) is named in the message (R11).
            return Error(
                ReadRequestId(request),
                RpcCodeFor(failure.Code),
                ActionFailureMessage(failure),
                failure.Code.ToProtocolCode());
        }
        catch (Exception exception)
        {
            return Error(null, InternalErrorCode, exception.Message, "INVALID_REQUEST");
        }
    }

    /// <summary>Releases the control session when a pipe connection closes (R33).</summary>
    public void ReleaseSession(string sessionId) => _control.Release(sessionId);

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

    private async Task<JsonElement> HandleRequestAsync(JsonElement request, string sessionId)
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
            "hello" => HandleHello(id, request, sessionId),
            "status" => HandleStatus(id),
            "windows.list" => HandleWindowsList(id),
            "window.select" => await HandleWindowSelectAsync(id, request).ConfigureAwait(false),
            "snapshot" => HandleSnapshot(id, request),
            "action" => await HandleActionAsync(id, request).ConfigureAwait(false),
            "action.batch" => await HandleBatchAsync(id, request).ConfigureAwait(false),
            "find" => await HandleFindAsync(id, request).ConfigureAwait(false),
            "wait" => await HandleWaitAsync(id, request).ConfigureAwait(false),
            "read_text" => await HandleReadTextAsync(id, request).ConfigureAwait(false),
            "stop" => HandleStop(id),
            var method => Error(id, MethodNotFoundCode, $"Method not found: {method}", "METHOD_NOT_FOUND"),
        };
    }

    private JsonElement HandleHello(JsonNode? id, JsonElement request, string sessionId)
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

        // R33: hello claims the single control session for this connection.
        // Token and version validation above must pass first so a failed
        // handshake can never hold the lock.
        if (!_control.TryAcquire(sessionId))
        {
            return Error(
                id,
                ActionErrorCode,
                "Another client holds the control session.",
                "LOCKED_BY_OTHER_SESSION");
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

    /// <summary>
    /// Aborts the current operation. The session and connection stay alive so
    /// the agent can observe and resume (R35); stopping with nothing running is
    /// still a success.
    /// </summary>
    private JsonElement HandleStop(JsonNode? id)
    {
        _control.Stop();
        return Success(id, new StopResult { Ok = true });
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

        if (_desktop.IsLocked())
        {
            return DesktopUnavailableError(id);
        }

        var result = await _selection
            .SelectAsync(criteria, _control.Token)
            .ConfigureAwait(false);

        return FromSelectResult(id, result);
    }

    private JsonElement HandleSnapshot(JsonNode? id, JsonElement request)
    {
        if (!request.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            return Error(id, InvalidRequestCode, "snapshot params must be an object.", "INVALID_REQUEST");
        }

        if (!parameters.TryGetProperty("windowId", out var windowIdElement)
            || windowIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(windowIdElement.GetString()))
        {
            return Error(
                id,
                InvalidRequestCode,
                "windowId is required for a snapshot.",
                "INVALID_REQUEST");
        }

        var mode = _defaultMode;
        if (parameters.TryGetProperty("mode", out var modeElement)
            && (modeElement.ValueKind != JsonValueKind.String
                || !TryParseSnapshotMode(modeElement.GetString(), out mode)))
        {
            return Error(
                id,
                InvalidRequestCode,
                "snapshot mode must be one of a11y, vision, or both.",
                "INVALID_REQUEST");
        }

        if (!TryReadOptionalInt(
                parameters,
                "maxDepth",
                _defaultMaxDepth,
                minimum: 1,
                maximum: MaxSnapshotDepth,
                out var maxDepth)
            || !TryReadOptionalInt(
                parameters,
                "maxNodes",
                _defaultMaxNodes,
                minimum: 1,
                maximum: MaxSnapshotNodes,
                out var maxNodes))
        {
            return Error(
                id,
                InvalidRequestCode,
                "maxDepth and maxNodes must be positive integers within the host limits.",
                "INVALID_REQUEST");
        }

        var image = false;
        if (parameters.TryGetProperty("image", out var imageElement))
        {
            if (imageElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Error(
                    id,
                    InvalidRequestCode,
                    "image must be a boolean.",
                    "INVALID_REQUEST");
            }

            image = imageElement.GetBoolean();
        }

        SnapshotRegion? region = null;
        if (parameters.TryGetProperty("region", out var regionElement))
        {
            if (!TryParseSnapshotRegion(regionElement, out var parsedRegion))
            {
                return Error(
                    id,
                    InvalidRequestCode,
                    "region must be an object with non-negative x and y and positive width and height integers.",
                    "INVALID_REQUEST");
            }

            region = parsedRegion;
        }

        if (_desktop.IsLocked())
        {
            return DesktopUnavailableError(id);
        }

        var windowId = windowIdElement.GetString()!;
        if (!_registry.TryResolve(windowId, out var resolved) || resolved is null)
        {
            // Resolution failed for death, identity change, or a newly
            // matching privacy exclusion; the target must not stay selected.
            _selection.ClearIfSelected(windowId);
            _snapshots.Clear(windowId);
            return Error(id, ActionErrorCode, "The window is gone.", "WINDOW_GONE");
        }

        Rect? captureRegion = null;
        if (region is { } requested)
        {
            if (!_snapshots.TryGetState(windowId, out var state)
                || state?.Image is not { } imageMetadata)
            {
                return Error(
                    id,
                    InvalidRequestCode,
                    "A region snapshot requires a previous snapshot with an image.",
                    "INVALID_REQUEST");
            }

            if (!SameBounds(state.WindowBounds, resolved.Info.Bounds))
            {
                return Error(
                    id,
                    ActionErrorCode,
                    "The window moved or resized since the snapshot; take a new snapshot before cropping.",
                    "STALE_SNAPSHOT");
            }

            var scale = imageMetadata.Scale;
            if (!double.IsFinite(scale) || scale <= 0)
            {
                return Error(
                    id,
                    InvalidRequestCode,
                    "The previous snapshot image has no usable scale.",
                    "INVALID_REQUEST");
            }

            var imageWidth = imageMetadata.SourceBounds.Width / scale;
            var imageHeight = imageMetadata.SourceBounds.Height / scale;
            if (requested.X + requested.Width > imageWidth + 0.5
                || requested.Y + requested.Height > imageHeight + 0.5)
            {
                return Error(
                    id,
                    InvalidRequestCode,
                    "region must lie inside the previous snapshot image.",
                    "INVALID_REQUEST");
            }

            var physical = new Rect(
                (int)Math.Round(imageMetadata.SourceBounds.X + (requested.X * scale)),
                (int)Math.Round(imageMetadata.SourceBounds.Y + (requested.Y * scale)),
                Math.Max(1, (int)Math.Round(requested.Width * scale)),
                Math.Max(1, (int)Math.Round(requested.Height * scale)));
            if (!ContainsRect(resolved.Info.Bounds, physical))
            {
                return Error(
                    id,
                    InvalidRequestCode,
                    "region must lie inside the selected window.",
                    "INVALID_REQUEST");
            }

            captureRegion = physical;

            // A crop is an image request; an a11y-only caller still gets the
            // image it asked to crop.
            image = true;
            if (mode == SnapshotMode.A11y)
            {
                mode = SnapshotMode.Vision;
            }
        }

        var options = new SnapshotOptions
        {
            Mode = mode,
            MaxDepth = maxDepth,
            MaxNodes = maxNodes,
            Image = image,
            ImageLongEdge = _defaultImageLongEdge,
            MaxImageLongEdge = _maxImageLongEdge,
            CaptureRegion = captureRegion,
        };

        // The registry's freshly resolved bounds are authoritative for both the
        // image's sourceBounds and the scale the client maps coordinates with.
        var capture = _snapshotter.Capture(
            resolved.Hwnd,
            resolved.Info.Bounds,
            options,
            _control.Token);

        // The window can die while the tree is being walked; never report a
        // snapshot for a window the registry can no longer resolve.
        if (!_registry.TryResolve(windowId, out var current) || current is null)
        {
            _selection.ClearIfSelected(windowId);
            _snapshots.Clear(windowId);
            return Error(id, ActionErrorCode, "The window is gone.", "WINDOW_GONE");
        }

        capture.Snapshot.Window = current.Info;

        // Save the bounds the capture actually used. Using the post-walk
        // bounds would make the live and saved bounds agree while the image
        // still maps against the pre-walk source rectangle, letting a
        // mid-walk move slip past the image-space stale check.
        _snapshots.Save(windowId, capture.Snapshot, capture.NodeMap, resolved.Info.Bounds);
        return Success(id, capture.Snapshot);
    }

    private static bool TryParseSnapshotMode(string? value, out SnapshotMode mode)
    {
        switch (value)
        {
            case "a11y":
                mode = SnapshotMode.A11y;
                return true;
            case "vision":
                mode = SnapshotMode.Vision;
                return true;
            case "both":
                mode = SnapshotMode.Both;
                return true;
            default:
                mode = SnapshotMode.Both;
                return false;
        }
    }

    private static bool TryParseSnapshotRegion(JsonElement element, out SnapshotRegion region)
    {
        region = default;

        if (element.ValueKind != JsonValueKind.Object
            || !TryReadIntProperty(element, "x", out var x)
            || !TryReadIntProperty(element, "y", out var y)
            || !TryReadIntProperty(element, "width", out var width)
            || !TryReadIntProperty(element, "height", out var height)
            || x < 0
            || y < 0
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        region = new SnapshotRegion(x, y, width, height);
        return true;
    }

    private static bool TryReadIntProperty(JsonElement element, string name, out int value)
    {
        value = 0;

        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool SameBounds(Rect left, Rect right)
        => left.X == right.X
            && left.Y == right.Y
            && left.Width == right.Width
            && left.Height == right.Height;

    private static bool ContainsRect(Rect outer, Rect inner)
        => inner.X >= outer.X
            && inner.Y >= outer.Y
            && inner.X + inner.Width <= outer.X + outer.Width
            && inner.Y + inner.Height <= outer.Y + outer.Height;

    private static bool TryReadOptionalInt(
        JsonElement parameters,
        string propertyName,
        int defaultValue,
        int minimum,
        int maximum,
        out int value)
    {
        value = defaultValue;

        if (!parameters.TryGetProperty(propertyName, out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private async Task<JsonElement> HandleActionAsync(JsonNode? id, JsonElement request)
    {
        if (!TryReadParams(request, out var parameters))
        {
            return Error(id, InvalidRequestCode, "action params must be an object.", "INVALID_REQUEST");
        }

        // Parsing happens before any desktop check or service call, so a
        // malformed request can never reach input (R9).
        var kind = ReadRequiredString(parameters, "kind");

        switch (kind)
        {
            case "focus":
                return await HandleFocusActionAsync(id, parameters).ConfigureAwait(false);

            case "click":
            {
                var clickRequest = ParseClickRequest(parameters);
                return await ExecuteActionAsync(
                    id,
                    token => _click.ExecuteAsync(clickRequest, token)).ConfigureAwait(false);
            }

            case "type":
            {
                var typeRequest = ParseTypeRequest(parameters);
                return await ExecuteActionAsync(
                    id,
                    token => _type.ExecuteAsync(typeRequest, token)).ConfigureAwait(false);
            }

            case "key":
            {
                var keyRequest = ParseKeyRequest(parameters);
                return await ExecuteActionAsync(
                    id,
                    token => _key.ExecuteAsync(keyRequest, token)).ConfigureAwait(false);
            }

            case "scroll":
            {
                var scrollRequest = ParseScrollRequest(parameters);
                return await ExecuteActionAsync(
                    id,
                    token => _scroll.ExecuteAsync(scrollRequest, token)).ConfigureAwait(false);
            }

            default:
                throw Invalid($"Unsupported action kind: {kind}.");
        }
    }

    /// <summary>
    /// The M1 focus action: an explicit windowId is selected and focused. Its
    /// request shape and behavior are unchanged (R8).
    /// </summary>
    private async Task<JsonElement> HandleFocusActionAsync(JsonNode? id, JsonElement parameters)
    {
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

        if (_desktop.IsLocked())
        {
            return DesktopUnavailableError(id);
        }

        var result = await _selection
            .SelectAsync(windowIdElement.GetString()!, _control.Token)
            .ConfigureAwait(false);

        return FromSelectResult(id, result);
    }

    private async Task<JsonElement> ExecuteActionAsync(
        JsonNode? id,
        Func<CancellationToken, Task<ActionResult>> execute)
    {
        if (_desktop.IsLocked())
        {
            return DesktopUnavailableError(id);
        }

        // The token is read when the action starts so a stop that arrived
        // while the request was being parsed still aborts it.
        var result = await execute(_control.Token).ConfigureAwait(false);
        return Success(id, result);
    }

    private async Task<JsonElement> HandleFindAsync(JsonNode? id, JsonElement request)
    {
        if (!TryReadParams(request, out var parameters))
        {
            return Error(id, InvalidRequestCode, "find params must be an object.", "INVALID_REQUEST");
        }

        var findRequest = ParseFindRequest(parameters);

        if (_desktop.IsLocked())
        {
            return DesktopUnavailableError(id);
        }

        var result = await _find
            .ExecuteAsync(findRequest, _control.Token)
            .ConfigureAwait(false);

        return Success(id, result);
    }

    private async Task<JsonElement> HandleWaitAsync(JsonNode? id, JsonElement request)
    {
        if (!TryReadParams(request, out var parameters))
        {
            return Error(id, InvalidRequestCode, "wait params must be an object.", "INVALID_REQUEST");
        }

        var waitRequest = ParseWaitRequest(parameters);

        if (_desktop.IsLocked())
        {
            return DesktopUnavailableError(id);
        }

        var result = await _wait
            .ExecuteAsync(waitRequest, _control.Token)
            .ConfigureAwait(false);

        // The host condition is mapped back to the shared discriminated union,
        // because System.Text.Json cannot serialize the abstraction directly.
        return Success(id, WaitResultToProtocol(result));
    }

    private async Task<JsonElement> HandleReadTextAsync(JsonNode? id, JsonElement request)
    {
        if (!TryReadParams(request, out var parameters))
        {
            return Error(id, InvalidRequestCode, "read_text params must be an object.", "INVALID_REQUEST");
        }

        var readTextRequest = ParseReadTextRequest(parameters);

        if (_desktop.IsLocked())
        {
            return DesktopUnavailableError(id);
        }

        var result = await _readText
            .ExecuteAsync(readTextRequest, _control.Token)
            .ConfigureAwait(false);

        return Success(id, result);
    }

    private async Task<JsonElement> HandleBatchAsync(JsonNode? id, JsonElement request)
    {
        if (!TryReadParams(request, out var parameters))
        {
            return Error(id, InvalidRequestCode, "action.batch params must be an object.", "INVALID_REQUEST");
        }

        var batchRequest = ParseBatchRequest(parameters);

        if (_desktop.IsLocked())
        {
            return DesktopUnavailableError(id);
        }

        var result = await _batch
            .ExecuteAsync(batchRequest, _control.Token)
            .ConfigureAwait(false);

        return Success(id, BatchResultToProtocol(result));
    }

    /// <summary>
    /// A locked or secure desktop fails closed with no input, focus, or capture
    /// attempted (DESIGN §8 hard rule 2). Status still reports the lock state.
    /// </summary>
    private static JsonElement DesktopUnavailableError(JsonNode? id)
        => Error(
            id,
            ActionErrorCode,
            "The desktop is locked; no operation was attempted.",
            "DESKTOP_UNAVAILABLE");

    private static JsonElement FromSelectResult(JsonNode? id, SelectResult result)
    {
        if (result.Error is { } error)
        {
            return Error(
                id,
                RpcCodeFor(error),
                result.Message
                    ?? SelectErrorMessages.Describe(error, result.Candidates?.Count ?? 0),
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

    /// <summary>
    /// Reads the params object for a method. A missing or non-object value is
    /// the caller's error, reported as INVALID_REQUEST before any work runs.
    /// </summary>
    private static bool TryReadParams(JsonElement request, out JsonElement parameters)
        => request.TryGetProperty("params", out parameters)
            && parameters.ValueKind == JsonValueKind.Object;

    private static ActionFailedException Invalid(string message)
        => new(ErrorCode.InvalidRequest, message);

    /// <summary>
    /// Rejects properties the shared strict schemas do not allow, so a typo or
    /// a platform-specific field cannot be silently ignored.
    /// </summary>
    private static void EnsureAllowedFields(JsonElement element, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                throw Invalid($"Unknown field \"{property.Name}\".");
            }
        }
    }

    private static string? ReadOptionalString(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(element.GetString()))
        {
            throw Invalid($"{name} must be a non-empty string.");
        }

        return element.GetString();
    }

    private static string ReadRequiredString(JsonElement parameters, string name)
        => ReadOptionalString(parameters, name)
            ?? throw Invalid($"{name} is required.");

    private static bool? ReadOptionalBool(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid($"{name} must be a boolean.");
        }

        return element.GetBoolean();
    }

    private static int? ReadOptionalPositiveInt(
        JsonElement parameters,
        string name,
        int maximum = int.MaxValue)
    {
        if (!parameters.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)
            || value < 1
            || value > maximum)
        {
            throw Invalid(
                maximum == int.MaxValue
                    ? $"{name} must be a positive integer."
                    : $"{name} must be an integer between 1 and {maximum}.");
        }

        return value;
    }

    private static double? ReadOptionalFiniteDouble(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var value)
            || !double.IsFinite(value))
        {
            throw Invalid($"{name} must be a finite number.");
        }

        return value;
    }

    private static CoordinateSpace ReadSpace(JsonElement parameters)
    {
        var value = ReadOptionalString(parameters, "space");
        return value switch
        {
            null or "image" => CoordinateSpace.Image,
            "screen" => CoordinateSpace.Screen,
            _ => throw Invalid("space must be \"image\" or \"screen\"."),
        };
    }

    private static MouseButton ReadButton(JsonElement parameters)
    {
        var value = ReadOptionalString(parameters, "button");
        return value switch
        {
            null or "left" => MouseButton.Left,
            "middle" => MouseButton.Middle,
            "right" => MouseButton.Right,
            _ => throw Invalid("button must be \"left\", \"middle\", or \"right\"."),
        };
    }

    private static TypeMethod ReadTypeMethod(JsonElement parameters)
    {
        var value = ReadOptionalString(parameters, "method");
        return value switch
        {
            null or "paste" => TypeMethod.Paste,
            "value" => TypeMethod.Value,
            "keys" => TypeMethod.Keys,
            _ => throw Invalid("method must be \"paste\", \"value\", or \"keys\"."),
        };
    }

    private static ScrollDirection ReadDirection(JsonElement parameters)
    {
        var value = ReadRequiredString(parameters, "direction");
        return value switch
        {
            "up" => ScrollDirection.Up,
            "down" => ScrollDirection.Down,
            "left" => ScrollDirection.Left,
            "right" => ScrollDirection.Right,
            _ => throw Invalid("direction must be \"up\", \"down\", \"left\", or \"right\"."),
        };
    }

    /// <summary>
    /// Parses the shared expect array. An explicit <c>null</c> is treated as no
    /// predicates, matching the host's empty-list default.
    /// </summary>
    private static IReadOnlyList<ExpectPredicate> ParseExpect(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("expect", out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("expect must be an array of predicates.");
        }

        var predicates = new List<ExpectPredicate>();
        foreach (var predicate in element.EnumerateArray())
        {
            predicates.Add(ParseExpectPredicate(predicate));
        }

        return predicates;
    }

    private static ExpectPredicate ParseExpectPredicate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Each expect predicate must be an object.");
        }

        var kind = ReadRequiredString(element, "kind");
        switch (kind)
        {
            case "foreground":
                EnsureAllowedFields(element, "kind");
                return new ExpectForeground();

            case "node":
            {
                EnsureAllowedFields(element, "kind", "role", "name", "identifier", "focused");
                var role = ReadOptionalString(element, "role");
                var name = ReadOptionalString(element, "name");
                var identifier = ReadOptionalString(element, "identifier");
                var focused = ReadOptionalBool(element, "focused");

                // The shared schema rejects a selector-less node predicate;
                // the evaluator would otherwise match every node.
                if (role is null && name is null && identifier is null)
                {
                    throw Invalid("A node expect predicate requires a role, name, or identifier.");
                }

                return new ExpectNode
                {
                    Role = role,
                    Name = name,
                    Identifier = identifier,
                    Focused = focused,
                };
            }

            case "text":
            {
                EnsureAllowedFields(element, "kind", "role", "name", "identifier", "contains");
                return new ExpectText
                {
                    Role = ReadOptionalString(element, "role"),
                    Name = ReadOptionalString(element, "name"),
                    Identifier = ReadOptionalString(element, "identifier"),
                    Contains = ReadRequiredString(element, "contains"),
                };
            }

            default:
                throw Invalid($"Unsupported expect predicate kind: {kind}.");
        }
    }

    private static WaitCondition ParseCondition(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("condition", out var element)
            || element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("A wait condition object is required.");
        }

        var kind = ReadRequiredString(element, "kind");
        switch (kind)
        {
            case "window":
            {
                EnsureAllowedFields(element, "kind", "process", "title");
                var process = ReadOptionalString(element, "process");
                var title = ReadOptionalString(element, "title");
                if (process is null && title is null)
                {
                    throw Invalid("A window wait condition requires a process or a title.");
                }

                return new WaitWindowCondition { Process = process, Title = title };
            }

            case "node":
            {
                EnsureAllowedFields(element, "kind", "role", "name", "identifier");
                var role = ReadOptionalString(element, "role");
                var name = ReadOptionalString(element, "name");
                var identifier = ReadOptionalString(element, "identifier");
                if (role is null && name is null && identifier is null)
                {
                    throw Invalid("A node wait condition requires a role, name, or identifier.");
                }

                return new WaitNodeCondition
                {
                    Role = role,
                    Name = name,
                    Identifier = identifier,
                };
            }

            case "text":
            {
                EnsureAllowedFields(element, "kind", "role", "name", "identifier", "contains");
                return new WaitTextCondition
                {
                    Role = ReadOptionalString(element, "role"),
                    Name = ReadOptionalString(element, "name"),
                    Identifier = ReadOptionalString(element, "identifier"),
                    Contains = ReadRequiredString(element, "contains"),
                };
            }

            default:
                throw Invalid($"Unsupported wait condition kind: {kind}.");
        }
    }

    private static ClickRequest ParseClickRequest(JsonElement parameters)
    {
        EnsureAllowedFields(
            parameters,
            "kind",
            "nodeId",
            "x",
            "y",
            "text",
            "role",
            "space",
            "button",
            "count",
            "modifiers",
            "expect");

        var nodeId = ReadOptionalString(parameters, "nodeId");
        var x = ReadOptionalFiniteDouble(parameters, "x");
        var y = ReadOptionalFiniteDouble(parameters, "y");
        var text = ReadOptionalString(parameters, "text");
        var role = ReadOptionalString(parameters, "role");
        if (text is not null && text.Length > MaxSelectorTextLength)
        {
            throw Invalid($"text must be at most {MaxSelectorTextLength} characters.");
        }

        EnsureSingleClickTarget(nodeId, x, y, text, role);

        return new ClickRequest
        {
            NodeId = nodeId,
            X = x,
            Y = y,
            Text = text,
            Role = role,
            Space = ReadSpace(parameters),
            Button = ReadButton(parameters),
            Count = ReadOptionalPositiveInt(parameters, "count", MaxClickCount) ?? 1,
            Modifiers = ReadOptionalStringArray(parameters, "modifiers", maxItems: 4),
            Expect = ParseExpect(parameters),
        };
    }

    /// <summary>
    /// Reads an optional array of non-empty strings. A malformed array is
    /// rejected here so it can never reach an action.
    /// </summary>
    private static IReadOnlyList<string> ReadOptionalStringArray(
        JsonElement parameters,
        string name,
        int maxItems)
    {
        if (!parameters.TryGetProperty(name, out var element))
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{name} must be an array of strings.");
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(item.GetString()))
            {
                throw Invalid($"{name} must be an array of non-empty strings.");
            }

            values.Add(item.GetString()!);
        }

        if (values.Count > maxItems)
        {
            throw Invalid($"{name} must have at most {maxItems} entries.");
        }

        return values;
    }

    private static TypeRequest ParseTypeRequest(JsonElement parameters)
    {
        EnsureAllowedFields(parameters, "kind", "text", "method", "nodeId", "expect");

        var text = ReadRequiredString(parameters, "text");
        if (text.Length > MaxTypeTextLength)
        {
            throw Invalid(
                $"text must be at most {MaxTypeTextLength} characters.");
        }

        return new TypeRequest
        {
            Text = text,
            Method = ReadTypeMethod(parameters),
            NodeId = ReadOptionalString(parameters, "nodeId"),
            Expect = ParseExpect(parameters),
        };
    }

    private static KeyRequest ParseKeyRequest(JsonElement parameters)
    {
        EnsureAllowedFields(parameters, "kind", "keys", "expect");

        return new KeyRequest
        {
            Keys = ReadRequiredString(parameters, "keys"),
            Expect = ParseExpect(parameters),
        };
    }

    private static ScrollRequest ParseScrollRequest(JsonElement parameters)
    {
        EnsureAllowedFields(
            parameters,
            "kind",
            "nodeId",
            "x",
            "y",
            "space",
            "direction",
            "amount",
            "expect");

        var nodeId = ReadOptionalString(parameters, "nodeId");
        var x = ReadOptionalFiniteDouble(parameters, "x");
        var y = ReadOptionalFiniteDouble(parameters, "y");
        EnsureSingleTarget(nodeId, x, y);

        return new ScrollRequest
        {
            NodeId = nodeId,
            X = x,
            Y = y,
            Space = ReadSpace(parameters),
            Direction = ReadDirection(parameters),
            Amount = ReadOptionalPositiveInt(parameters, "amount") ?? 1,
            Expect = ParseExpect(parameters),
        };
    }

    /// <summary>
    /// A click targets exactly one of a node ID, an (x, y) pair, or text with
    /// an optional role, mirroring the shared schema.
    /// </summary>
    private static void EnsureSingleClickTarget(
        string? nodeId,
        double? x,
        double? y,
        string? text,
        string? role)
    {
        var hasNode = nodeId is not null;
        var hasX = x is not null;
        var hasY = y is not null;
        var hasText = !string.IsNullOrWhiteSpace(text);
        var forms = (hasNode ? 1 : 0) + ((hasX && hasY) ? 1 : 0) + (hasText ? 1 : 0);

        if (hasX != hasY || forms != 1 || (role is not null && !hasText))
        {
            throw Invalid(
                "Provide exactly one of nodeId, x and y, or text with an optional role.");
        }
    }

    /// <summary>
    /// Scroll targets exactly one of a node ID or an (x, y) pair, mirroring
    /// the shared schema (R9).
    /// </summary>
    private static void EnsureSingleTarget(string? nodeId, double? x, double? y)
    {
        var hasNode = nodeId is not null;
        var hasX = x is not null;
        var hasY = y is not null;

        if (hasNode ? hasX || hasY : !(hasX && hasY))
        {
            throw Invalid("Provide exactly one of nodeId or x and y.");
        }
    }

    private static FindRequest ParseFindRequest(JsonElement parameters)
    {
        EnsureAllowedFields(parameters, "text", "role", "maxResults");

        return new FindRequest
        {
            Text = ReadOptionalString(parameters, "text"),
            Role = ReadOptionalString(parameters, "role"),
            MaxResults = ReadOptionalPositiveInt(parameters, "maxResults"),
        };
    }

    private static WaitRequest ParseWaitRequest(JsonElement parameters, bool isStep = false)
    {
        // A batch step is the wait request flattened with its kind discriminator,
        // while the top-level wait params must not carry one.
        EnsureAllowedFields(
            parameters,
            isStep
                ? new[] { "kind", "condition", "present", "timeoutMs", "pollMs" }
                : new[] { "condition", "present", "timeoutMs", "pollMs" });

        return new WaitRequest
        {
            Condition = ParseCondition(parameters),
            Present = ReadOptionalBool(parameters, "present") ?? true,
            TimeoutMs = ReadOptionalPositiveInt(parameters, "timeoutMs"),
            PollMs = ReadOptionalPositiveInt(parameters, "pollMs"),
        };
    }

    private static ReadTextRequest ParseReadTextRequest(JsonElement parameters, bool isStep = false)
    {
        EnsureAllowedFields(
            parameters,
            isStep
                ? new[] { "kind", "nodeId", "maxLength" }
                : new[] { "nodeId", "maxLength" });

        return new ReadTextRequest
        {
            NodeId = ReadRequiredString(parameters, "nodeId"),
            MaxLength = ReadOptionalPositiveInt(parameters, "maxLength"),
        };
    }

    private static BatchRequest ParseBatchRequest(JsonElement parameters)
    {
        EnsureAllowedFields(parameters, "steps", "snapshotBefore", "snapshotAfter");

        if (!parameters.TryGetProperty("steps", out var stepsElement)
            || stepsElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("A batch requires a steps array.");
        }

        var steps = new List<ActionStep>();
        foreach (var step in stepsElement.EnumerateArray())
        {
            steps.Add(ParseStep(step));
        }

        if (steps.Count == 0)
        {
            throw Invalid("A batch requires at least one step.");
        }

        return new BatchRequest
        {
            Steps = steps,
            SnapshotBefore = ReadOptionalBool(parameters, "snapshotBefore") ?? false,
            SnapshotAfter = ReadOptionalBool(parameters, "snapshotAfter") ?? false,
        };
    }

    private static ActionStep ParseStep(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Each batch step must be an object.");
        }

        var kind = ReadRequiredString(element, "kind");
        return kind switch
        {
            "click" => new ClickStep { Request = ParseClickRequest(element) },
            "type" => new TypeStep { Request = ParseTypeRequest(element) },
            "key" => new KeyStep { Request = ParseKeyRequest(element) },
            "scroll" => new ScrollStep { Request = ParseScrollRequest(element) },
            "wait" => new WaitStep { Request = ParseWaitRequest(element, isStep: true) },
            "readText" => new ReadTextStep
            {
                Request = ParseReadTextRequest(element, isStep: true),
            },
            _ => throw Invalid($"Unsupported batch step kind: {kind}."),
        };
    }

    /// <summary>
    /// Maps a host <see cref="WaitResult"/> onto the shared protocol shape.
    /// The condition is a discriminated union, so its kind and selector fields
    /// are written explicitly instead of relying on polymorphism.
    /// </summary>
    private static JsonObject WaitResultToProtocol(WaitResult result)
    {
        var node = new JsonObject
        {
            ["condition"] = ConditionToProtocol(result.Condition),
            ["elapsedMs"] = result.ElapsedMs,
        };

        if (result.Window is not null)
        {
            node["window"] = JsonSerializer.SerializeToNode(result.Window, ProtocolJson.Options);
        }

        return node;
    }

    private static JsonObject ConditionToProtocol(WaitCondition condition)
    {
        switch (condition)
        {
            case WaitWindowCondition window:
            {
                var node = new JsonObject { ["kind"] = "window" };
                if (window.Process is not null)
                {
                    node["process"] = window.Process;
                }

                if (window.Title is not null)
                {
                    node["title"] = window.Title;
                }

                return node;
            }

            case WaitNodeCondition nodeCondition:
            {
                var node = new JsonObject { ["kind"] = "node" };
                AddSelector(node, nodeCondition.Role, nodeCondition.Name, nodeCondition.Identifier);
                return node;
            }

            case WaitTextCondition text:
            {
                var node = new JsonObject { ["kind"] = "text" };
                AddSelector(node, text.Role, text.Name, text.Identifier);
                node["contains"] = text.Contains;
                return node;
            }

            default:
                throw new InvalidOperationException("The wait condition is not supported.");
        }
    }

    private static void AddSelector(
        JsonObject node,
        string? role,
        string? name,
        string? identifier)
    {
        if (role is not null)
        {
            node["role"] = role;
        }

        if (name is not null)
        {
            node["name"] = name;
        }

        if (identifier is not null)
        {
            node["identifier"] = identifier;
        }
    }

    /// <summary>
    /// Maps a batch result onto the shared protocol shape. <see cref="BatchStepResult.Code"/>
    /// is the host error enum, so it is written with <c>ToProtocolCode()</c>.
    /// </summary>
    private static JsonObject BatchResultToProtocol(BatchResult result)
    {
        var steps = new JsonArray();
        foreach (var step in result.Steps)
        {
            var item = new JsonObject
            {
                ["index"] = step.Index,
                ["kind"] = step.Kind,
                ["ok"] = step.Ok,
            };

            if (step.Code is { } code)
            {
                item["code"] = code.ToProtocolCode();
            }

            if (step.Message is { } message)
            {
                item["message"] = message;
            }

            if (step.Data is { } data)
            {
                item["data"] = StepDataToProtocol(data);
            }

            steps.Add(item);
        }

        var batch = new JsonObject { ["steps"] = steps };

        if (result.FailedIndex is { } failedIndex)
        {
            batch["failedIndex"] = failedIndex;
        }

        if (result.SnapshotBefore is { } before)
        {
            batch["snapshotBefore"] = JsonSerializer.SerializeToNode(before, ProtocolJson.Options);
        }

        if (result.SnapshotAfter is { } after)
        {
            batch["snapshotAfter"] = JsonSerializer.SerializeToNode(after, ProtocolJson.Options);
        }

        if (result.Warning is { } warning)
        {
            batch["warning"] = warning;
        }

        return batch;
    }

    private static JsonNode? StepDataToProtocol(object data)
        => data switch
        {
            WaitResult wait => WaitResultToProtocol(wait),
            _ => JsonSerializer.SerializeToNode(data, ProtocolJson.Options),
        };

    private static string ActionFailureMessage(ActionFailedException failure)
        => failure.FailedPredicate is { } predicate
            && !failure.Message.Contains(predicate, StringComparison.Ordinal)
                ? $"{failure.Message} (predicate: {predicate})"
                : failure.Message;

    private static int RpcCodeFor(ErrorCode code) => code switch
    {
        ErrorCode.InvalidRequest => InvalidRequestCode,
        ErrorCode.MethodNotFound => MethodNotFoundCode,
        ErrorCode.Unauthorized => UnauthorizedCode,
        ErrorCode.FrameTooLarge => -32002,
        ErrorCode.HostUnavailable => -32003,
        _ => ActionErrorCode,
    };

    private static JsonNode? ReadRequestId(JsonElement request)
        => request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("id", out var id)
            ? JsonNode.Parse(id.GetRawText())
            : null;

    private static JsonElement Success(JsonNode? id, object result)
    {
        // Results that are already protocol-shaped nodes (the wait and batch
        // mappers) are used as-is; everything else serializes by runtime type.
        var resultNode = result is JsonNode node
            ? node
            : JsonSerializer.SerializeToNode(result, ProtocolJson.Options);

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = resultNode,
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

    /// <summary>The <c>{ ok: true }</c> result of <c>stop</c>.</summary>
    private sealed class StopResult
    {
        public bool Ok { get; init; }
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
