using System.Text;
using KennectComputer.Host.Ipc;

namespace KennectComputer.Host.Tests;

public sealed class FrameCodecTests
{
    [Fact]
    public async Task ReadsAFragmentedFrame()
    {
        var payload = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}");
        var stream = new OneByteAtATimeStream(FrameCodec.Frame(payload));
        var read = await FrameCodec.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task RejectsAnOversizedFrame()
    {
        var header = BitConverter.GetBytes(16 * 1024 * 1024 + 1);
        var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<FrameTooLargeException>(() => FrameCodec.ReadAsync(stream, CancellationToken.None));
    }

    private sealed class OneByteAtATimeStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length || count == 0)
            {
                return 0;
            }

            buffer[offset] = data[_position++];
            return 1;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_position >= data.Length || buffer.Length == 0)
            {
                return 0;
            }

            buffer[0] = data[_position++];
            return 1;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
