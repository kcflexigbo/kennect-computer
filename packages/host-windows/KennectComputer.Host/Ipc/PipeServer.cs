using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;

using KennectComputer.Host.Auth;
using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Ipc;

/// <summary>
/// Accepts framed JSON-RPC connections on the per-user named pipe.
/// Each connection is served independently and must authenticate with a
/// valid <c>hello</c> before any other method; an UNAUTHORIZED response closes it.
/// </summary>
public sealed class PipeServer
{
    /// <summary>Maximum number of connections served at once; excess clients get HOST_UNAVAILABLE.</summary>
    public const int MaxConcurrentClients = 8;

    private const string PipePathPrefix = @"\\.\pipe\";
    private const int MaxServerInstances = MaxConcurrentClients + 1;

    private readonly string _pipeName;
    private readonly TokenStore _tokens;
    private readonly JsonRpcDispatcher _dispatcher;

    public PipeServer(string pipeName, TokenStore tokens, JsonRpcDispatcher dispatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = NormalizePipeName(pipeName);
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Builds <c>\\.\pipe\kennect-computer-&lt;sanitized user&gt;</c>.</summary>
    public static string ForUser(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        var sanitized = new string(userName
            .Trim()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '-')
            .ToArray());

        if (sanitized.Length == 0)
        {
            sanitized = "user";
        }

        return $"{PipePathPrefix}kennect-computer-{sanitized}";
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var clients = new ConcurrentDictionary<long, Task>();
        long nextClientId = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // CurrentUserOnly applies a DACL that grants access to the current
                // user only, matching the token file's ACL. One extra server
                // instance lets the accept loop turn away overflow clients.
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    MaxServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    break;
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                if (clients.Count >= MaxConcurrentClients)
                {
                    await RejectClientAsync(pipe, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var clientId = Interlocked.Increment(ref nextClientId);
                var clientTask = HandleClientAsync(pipe, cancellationToken);
                clients[clientId] = clientTask;
                RemoveWhenComplete(clients, clientId, clientTask);
            }
        }
        finally
        {
            await Task.WhenAll(clients.Values).ConfigureAwait(false);
        }
    }

    /// <summary>Removes a finished client from the active set so tracking stays bounded.</summary>
    private static void RemoveWhenComplete(
        ConcurrentDictionary<long, Task> clients,
        long clientId,
        Task clientTask)
    {
        _ = clientTask.ContinueWith(
            completed =>
            {
                clients.TryRemove(clientId, out _);

                // Observe a fault so it cannot surface later as an unobserved exception.
                _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task RejectClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await WriteResponseAsync(
                pipe,
                JsonRpcDispatcher.HostUnavailableError(
                    $"The host is already serving {MaxConcurrentClients} clients."),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            var authenticated = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[]? frame;
                    try
                    {
                        frame = await FrameCodec.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                    }
                    catch (FrameTooLargeException exception)
                    {
                        await WriteResponseAsync(
                            pipe,
                            JsonRpcDispatcher.FrameTooLargeError(exception.Message),
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (frame is null)
                    {
                        return;
                    }

                    JsonElement response;
                    try
                    {
                        using var document = JsonDocument.Parse(frame);
                        var request = document.RootElement;
                        var method = GetMethod(request);

                        if (method == "hello")
                        {
                            if (!HasValidToken(request))
                            {
                                response = JsonRpcDispatcher.UnauthorizedError(
                                    "The session token is missing, stale, or invalid.",
                                    GetId(request));
                            }
                            else
                            {
                                response = await _dispatcher.HandleAsync(request.Clone()).ConfigureAwait(false);
                                authenticated = response.TryGetProperty("result", out _);
                            }
                        }
                        else if (!authenticated)
                        {
                            response = JsonRpcDispatcher.UnauthorizedError(
                                "Authenticate with hello before calling other methods.",
                                GetId(request));
                        }
                        else
                        {
                            response = await _dispatcher.HandleAsync(request.Clone()).ConfigureAwait(false);
                        }
                    }
                    catch (JsonException exception)
                    {
                        response = JsonRpcDispatcher.ParseError(exception.Message);
                    }

                    await WriteResponseAsync(pipe, response, cancellationToken).ConfigureAwait(false);

                    if (JsonRpcDispatcher.IsUnauthorized(response))
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Host shutdown.
            }
            catch (IOException)
            {
                // The client disconnected mid-frame.
            }
            catch (Exception)
            {
                // A single client must never fault the host's accept loop.
            }
        }
    }

    private bool HasValidToken(JsonElement request)
        => request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("params", out var parameters)
            && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("token", out var token)
            && token.ValueKind == JsonValueKind.String
            && _tokens.Validate(token.GetString());

    private static string? GetMethod(JsonElement request)
        => request.ValueKind == JsonValueKind.Object
            && request.TryGetProperty("method", out var method)
            && method.ValueKind == JsonValueKind.String
                ? method.GetString()
                : null;

    private static JsonNode? GetId(JsonElement request)
        => request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out var id)
            ? JsonNode.Parse(id.GetRawText())
            : null;

    private static async Task WriteResponseAsync(
        Stream stream,
        JsonElement response,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, ProtocolJson.Options);
        await FrameCodec.WriteAsync(stream, payload, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizePipeName(string pipeName)
        => pipeName.StartsWith(PipePathPrefix, StringComparison.OrdinalIgnoreCase)
            ? pipeName[PipePathPrefix.Length..]
            : pipeName;
}
