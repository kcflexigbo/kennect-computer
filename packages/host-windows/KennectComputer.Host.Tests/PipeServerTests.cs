using System.IO.Pipes;
using System.Text.Json;
using KennectComputer.Host.Auth;
using KennectComputer.Host.Ipc;

namespace KennectComputer.Host.Tests;

public sealed class PipeServerTests
{
    [Fact]
    public async Task ServesAuthenticatedRequestsAndRejectsOversizedFrames()
    {
        await using var harness = ServerHarness.Start();

        await using var client = await harness.ConnectAsync();
        var hello = await RequestAsync(client, HelloJson(harness.Token), harness.Cancellation);
        Assert.Equal("windows", hello.GetProperty("result").GetProperty("platform").GetString());

        var status = await RequestAsync(client, StatusJson(id: 2), harness.Cancellation);
        Assert.Equal("windows", status.GetProperty("result").GetProperty("platform").GetString());
        Assert.False(status.GetProperty("result").GetProperty("locked").GetBoolean());

        await client.WriteAsync(BitConverter.GetBytes(FrameCodec.MaxFrameBytes + 1), harness.Cancellation);
        await client.FlushAsync(harness.Cancellation);
        var oversized = await ReadResponseAsync(client, harness.Cancellation);
        Assert.Equal(
            "FRAME_TOO_LARGE",
            oversized.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.Null(await FrameCodec.ReadAsync(client, harness.Cancellation));
    }

    [Fact]
    public async Task RequiresHelloBeforeOtherMethodsAndRejectsStaleTokens()
    {
        await using var harness = ServerHarness.Start();

        await using (var unauthenticated = await harness.ConnectAsync())
        {
            var status = await RequestAsync(unauthenticated, StatusJson(id: 1), harness.Cancellation);
            Assert.Equal(
                "UNAUTHORIZED",
                status.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
            Assert.Null(await FrameCodec.ReadAsync(unauthenticated, harness.Cancellation));
        }

        await using (var stale = await harness.ConnectAsync())
        {
            var hello = await RequestAsync(stale, HelloJson("stale-token"), harness.Cancellation);
            Assert.Equal(
                "UNAUTHORIZED",
                hello.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
            Assert.Null(await FrameCodec.ReadAsync(stale, harness.Cancellation));
        }
    }

    private static string HelloJson(string token)
        => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "hello",
            @params = new
            {
                token,
                protocolVersion = 1,
                clientName = "pipe-test",
            },
        });

    private static string StatusJson(int id)
        => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "status",
            @params = new { },
        });

    private static async Task<JsonElement> RequestAsync(
        Stream stream,
        string json,
        CancellationToken cancellationToken)
    {
        await FrameCodec.WriteAsync(stream, System.Text.Encoding.UTF8.GetBytes(json), cancellationToken);
        return await ReadResponseAsync(stream, cancellationToken);
    }

    private static async Task<JsonElement> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var frame = await FrameCodec.ReadAsync(stream, cancellationToken);
        Assert.NotNull(frame);
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.Clone();
    }

    private sealed class ServerHarness : IAsyncDisposable
    {
        private readonly string _pipeName;
        private readonly string _stateDirectory;
        private readonly CancellationTokenSource _cancellation;
        private readonly Task _run;

        private ServerHarness(
            string pipeName,
            string stateDirectory,
            string token,
            Task run,
            CancellationTokenSource cancellation)
        {
            _pipeName = pipeName;
            _stateDirectory = stateDirectory;
            Token = token;
            _run = run;
            _cancellation = cancellation;
        }

        public string Token { get; }

        public CancellationToken Cancellation => _cancellation.Token;

        public static ServerHarness Start()
        {
            var stateDirectory = Path.Combine(
                Path.GetTempPath(),
                "kennect-host-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stateDirectory);

            var tokens = TokenStore.CreateForTests(stateDirectory);
            var token = tokens.Rotate();
            var pipeName = "kennect-computer-test-" + Guid.NewGuid().ToString("N");
            var dispatcher = new JsonRpcDispatcher(tokens, new FakeMonitorProvider(), new FakeDesktopState());
            var server = new PipeServer(pipeName, tokens, dispatcher);
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = server.RunAsync(cancellation.Token);

            return new ServerHarness(pipeName, stateDirectory, token, run, cancellation);
        }

        public async Task<NamedPipeClientStream> ConnectAsync()
        {
            var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(5000, Cancellation);
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            try
            {
                await _run;
            }
            catch (OperationCanceledException)
            {
            }

            _cancellation.Dispose();
            try
            {
                Directory.Delete(_stateDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
