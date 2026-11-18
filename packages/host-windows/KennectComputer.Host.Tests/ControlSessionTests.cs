using System.Windows.Forms;

using KennectComputer.Host.Control;
using KennectComputer.Host.Input;

namespace KennectComputer.Host.Tests;

public sealed class ControlSessionTests
{
    [Fact]
    public void SecondSessionIsRejected()
    {
        var session = new ControlSession();
        Assert.True(session.TryAcquire("a"));
        Assert.False(session.TryAcquire("b"));
        session.Release("a");
        Assert.True(session.TryAcquire("b"));
    }

    [Fact]
    public void RepeatingHelloOnTheSameConnectionIsIdempotent()
    {
        var session = new ControlSession();
        Assert.True(session.TryAcquire("a"));
        Assert.True(session.TryAcquire("a"));
        Assert.Equal("a", session.Owner);
    }

    [Fact]
    public void AnotherConnectionCannotReleaseTheLock()
    {
        var session = new ControlSession();
        Assert.True(session.TryAcquire("a"));

        session.Release("b");

        Assert.True(session.IsHeld);
        Assert.False(session.TryAcquire("b"));
        session.Release("a");
        Assert.False(session.IsHeld);
    }

    [Fact]
    public void StopCancelsTheCurrentOperationAndArmsAFreshOne()
    {
        var session = new ControlSession();
        Assert.True(session.TryAcquire("a"));
        var operation = session.Token;
        Assert.False(operation.IsCancellationRequested);

        session.Stop();

        Assert.True(operation.IsCancellationRequested);
        Assert.False(session.Token.IsCancellationRequested);
        Assert.True(session.IsHeld);
        Assert.Equal("a", session.Owner);
        Assert.False(session.TryAcquire("b"));
    }

    [Fact]
    public void StopWithoutAnOperationIsSafeAndKeepsTheSessionUsable()
    {
        var session = new ControlSession();

        session.Stop();

        Assert.False(session.Token.IsCancellationRequested);
        Assert.True(session.TryAcquire("a"));
    }

    [Fact]
    public void ReleasingASessionCancelsItsInFlightOperation()
    {
        var session = new ControlSession();
        Assert.True(session.TryAcquire("a"));
        var operation = session.Token;

        session.Release("a");

        Assert.True(operation.IsCancellationRequested);
        Assert.False(session.IsHeld);
    }
}

public sealed class StopHotkeyTests
{
    [Fact]
    public void ParsesTheConfiguredStopHotkey()
    {
        Assert.True(StopHotkey.TryParse("Ctrl+Alt+Shift+Esc", out var modifiers, out var key));

        Assert.Equal(
            HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
            modifiers);
        Assert.Equal(Keys.Escape, key);
    }

    [Fact]
    public void ParsesDigitKeys()
    {
        Assert.True(StopHotkey.TryParse("Ctrl+1", out var modifiers, out var key));

        Assert.Equal(HotkeyModifiers.Control, modifiers);
        Assert.Equal(Keys.D1, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Nope")]
    [InlineData("Hyper+Esc")]
    [InlineData("Ctrl+12")]
    public void RejectsInvalidHotkeys(string text)
        => Assert.False(StopHotkey.TryParse(text, out _, out _));

    [Fact]
    public void ARegisteredHotkeyCancelsTheControlSession()
    {
        var session = new ControlSession();
        var registrar = new FakeHotkeyRegistrar();
        using var hotkey = new StopHotkey(session, registrar, "Ctrl+Alt+Shift+Esc");

        Assert.True(hotkey.Register((nint)1234, 7));

        Assert.Equal((nint)1234, registrar.Hwnd);
        Assert.Equal(7, registrar.Id);
        Assert.Equal(
            HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
            registrar.Modifiers);
        Assert.Equal(Keys.Escape, registrar.Key);

        var operation = session.Token;
        Assert.True(hotkey.HandleMessage(7));
        Assert.True(operation.IsCancellationRequested);

        // A different hotkey id is not the stop key and is not consumed.
        Assert.False(hotkey.HandleMessage(8));
    }

    [Fact]
    public void AFailedRegistrationIsReportedWithoutThrowing()
    {
        var session = new ControlSession();
        var registrar = new FakeHotkeyRegistrar { RegisterResult = false };
        using var hotkey = new StopHotkey(session, registrar, "Ctrl+Alt+Shift+Esc");

        Assert.False(hotkey.Register((nint)1, 5));
        Assert.False(hotkey.IsRegistered);

        var operation = session.Token;
        Assert.False(hotkey.HandleMessage(5));
        Assert.False(operation.IsCancellationRequested);
    }

    [Fact]
    public void AnInvalidConfiguredHotkeyNeverRegisters()
    {
        var session = new ControlSession();
        var registrar = new FakeHotkeyRegistrar();
        using var hotkey = new StopHotkey(session, registrar, "Ctrl+NotAKey");

        Assert.False(hotkey.IsConfigured);
        Assert.False(hotkey.Register((nint)1, 5));
        Assert.False(registrar.Called);
    }

    [Fact]
    public void DisposeUnregistersAHotkeyThatWasRegistered()
    {
        var session = new ControlSession();
        var registrar = new FakeHotkeyRegistrar();
        var hotkey = new StopHotkey(session, registrar, "Ctrl+Alt+Shift+Esc");
        Assert.True(hotkey.Register((nint)99, 3));

        hotkey.Dispose();

        Assert.Equal((nint)99, registrar.UnregisterHwnd);
        Assert.Equal(3, registrar.UnregisterId);
    }
}

internal sealed class FakeHotkeyRegistrar : IHotkeyRegistrar
{
    public bool RegisterResult { get; init; } = true;

    public bool Called { get; private set; }

    public nint Hwnd { get; private set; }

    public int Id { get; private set; }

    public HotkeyModifiers Modifiers { get; private set; }

    public Keys Key { get; private set; }

    public nint UnregisterHwnd { get; private set; }

    public int UnregisterId { get; private set; }

    public bool Register(nint hwnd, int id, HotkeyModifiers modifiers, Keys key)
    {
        Called = true;
        Hwnd = hwnd;
        Id = id;
        Modifiers = modifiers;
        Key = key;
        return RegisterResult;
    }

    public bool Unregister(nint hwnd, int id)
    {
        UnregisterHwnd = hwnd;
        UnregisterId = id;
        return true;
    }
}
