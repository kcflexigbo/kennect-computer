using KennectComputer.Host.Tray;

namespace KennectComputer.Host.Tests;

public sealed class ExitRequestTests
{
    [Fact]
    public void RequestingExitInvokesTheShutdownCallback()
    {
        var calls = 0;
        var exit = new ExitRequest(() => calls++);

        exit.Request();

        Assert.Equal(1, calls);
        Assert.True(exit.IsRequested);
    }

    [Fact]
    public void RepeatedExitRequestsShutDownOnlyOnce()
    {
        var calls = 0;
        var exit = new ExitRequest(() => calls++);

        exit.Request();
        exit.Request();
        exit.Request();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentExitRequestsShutDownOnlyOnce()
    {
        var calls = 0;
        var exit = new ExitRequest(() => Interlocked.Increment(ref calls));

        await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(exit.Request)));

        Assert.Equal(1, Volatile.Read(ref calls));
    }
}
