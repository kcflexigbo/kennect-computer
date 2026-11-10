using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

using KennectComputer.Host.Auth;
using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Ipc;

/// <summary>
/// Accepts framed JSON-RPC connections on the per-user named pipe.
/// Each connection is served independently; an UNAUTHORIZED handshake closes it.
/// </summary>
public sealed class PipeServer
{
    private const string PipePathPrefix = @"\\.\pipe\";

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
        var clients = new ConcurrentBag<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

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

                clients.Add(HandleClientAsync(pipe, cancellationToken));
            }
        }
        finally
        {
            await Task.WhenAll(clients).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
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
                        response = await _dispatcher.HandleAsync(document.RootElement.Clone()).ConfigureAwait(false);
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
        }
    }

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
