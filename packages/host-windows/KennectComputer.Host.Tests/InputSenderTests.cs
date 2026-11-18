using KennectComputer.Host.Input;

namespace KennectComputer.Host.Tests;

/// <summary>
/// Pins the high-level input-to-event mapping (DESIGN §6.2, §12): each sender
/// call produces the exact ordered <see cref="InputEvent"/> list handed to the
/// <see cref="IInputApi"/> seam. A fake API stands in for <c>SendInput</c>, so
/// the suite never injects real input; a fake clipboard keeps every test away
/// from the real clipboard.
/// </summary>
public sealed class InputSenderTests
{
    [Fact]
    public void MoveToSendsOneAbsoluteMoveEventAtThePhysicalPoint()
    {
        var api = new FakeInputApi();
        var sender = new WindowsInputSender(api);

        sender.MoveTo(10, 20);

        var move = Assert.Single(api.Events);
        Assert.Equal(InputEventKind.MouseMove, move.Kind);
        Assert.Equal(10, move.X);
        Assert.Equal(20, move.Y);
        Assert.Equal(InputEventFlags.Absolute, move.Flags);
    }

    [Fact]
    public void ClickSendsMoveThenLeftDownUpTwiceInOrder()
    {
        var api = new FakeInputApi();
        var sender = new WindowsInputSender(api);

        sender.MoveTo(10, 20);
        sender.Click(MouseButton.Left, 2);

        Assert.Equal(
            new[]
            {
                InputEventKind.MouseMove,
                InputEventKind.MouseButtonDown,
                InputEventKind.MouseButtonUp,
                InputEventKind.MouseButtonDown,
                InputEventKind.MouseButtonUp,
            },
            api.Events.Select(input => input.Kind));
        Assert.Equal(10, api.Events[0].X);
        Assert.Equal(20, api.Events[0].Y);
        Assert.All(api.Events.Skip(1), input => Assert.Equal(MouseButton.Left, input.Button));
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Middle)]
    [InlineData(MouseButton.Right)]
    public void ClickSendsTheRequestedButtonForASingleClick(MouseButton button)
    {
        var api = new FakeInputApi();
        var sender = new WindowsInputSender(api);

        sender.Click(button, 1);

        Assert.Equal(
            new[] { InputEventKind.MouseButtonDown, InputEventKind.MouseButtonUp },
            api.Events.Select(input => input.Kind));
        Assert.All(api.Events, input => Assert.Equal(button, input.Button));
    }

    [Fact]
    public void ClickWithACountBelowOneIsRejectedBeforeSending()
    {
        var api = new FakeInputApi();
        var sender = new WindowsInputSender(api);

        Assert.Throws<ArgumentOutOfRangeException>(() => sender.Click(MouseButton.Left, 0));
        Assert.Empty(api.Events);
    }

    [Fact]
    public void VerticalScrollSendsOneWheelEventWithTheDelta()
    {
        var api = new FakeInputApi();
        var sender = new WindowsInputSender(api);

        sender.Scroll(-120, horizontal: false);

        var wheel = Assert.Single(api.Events);
        Assert.Equal(InputEventKind.MouseWheel, wheel.Kind);
        Assert.Equal(-120, wheel.Delta);
        Assert.Equal(InputEventFlags.None, wheel.Flags);
    }

    [Fact]
    public void HorizontalScrollMarksTheWheelEventAsHorizontal()
    {
        var api = new FakeInputApi();
        var sender = new WindowsInputSender(api);

        sender.Scroll(120, horizontal: true);

        var wheel = Assert.Single(api.Events);
        Assert.Equal(InputEventKind.MouseWheel, wheel.Kind);
        Assert.Equal(120, wheel.Delta);
        Assert.Equal(InputEventFlags.Horizontal, wheel.Flags);
    }

    [Fact]
    public void KeyDownAndKeyUpSendTheirVirtualKeys()
    {
        var api = new FakeInputApi();
        var sender = new WindowsInputSender(api);

        sender.KeyDown(0x11);
        sender.KeyUp(0x11);

        Assert.Equal(2, api.Events.Count);
        Assert.Equal(InputEventKind.KeyDown, api.Events[0].Kind);
        Assert.Equal((ushort)0x11, api.Events[0].VirtualKey);
        Assert.Equal(InputEventKind.KeyUp, api.Events[1].Kind);
        Assert.Equal((ushort)0x11, api.Events[1].VirtualKey);
    }

    [Fact]
    public void UnicodeTextSendsOneUnicodeEventPerUtf16CodeUnit()
    {
        var api = new FakeInputApi();
        var sender = new WindowsInputSender(api);

        sender.UnicodeText("漢a");

        Assert.Equal(2, api.Events.Count);
        Assert.Equal(InputEventKind.Unicode, api.Events[0].Kind);
        Assert.Equal('漢', api.Events[0].Unicode);
        Assert.Equal(InputEventFlags.Unicode, api.Events[0].Flags);
        Assert.Equal(InputEventKind.Unicode, api.Events[1].Kind);
        Assert.Equal('a', api.Events[1].Unicode);
        Assert.Equal(InputEventFlags.Unicode, api.Events[1].Flags);
    }

    [Fact]
    public void UnicodeTextSplitsASurrogatePairIntoSeparateCodeUnits()
    {
        // "𠮷" is a single scalar but two UTF-16 code units; SendInput's
        // KEYEVENTF_UNICODE path takes one code unit per event.
        var api = new FakeInputApi();
        var sender = new WindowsInputSender(api);

        sender.UnicodeText("𠮷");

        Assert.Equal(2, api.Events.Count);
        Assert.Equal(
            "𠮷",
            string.Concat(api.Events.Select(input => input.Unicode)));
    }

    [Fact]
    public void AClipboardFakeStoresAndReturnsTextWithoutTheRealClipboard()
    {
        IClipboard clipboard = new FakeClipboard();

        Assert.Null(clipboard.GetText());

        clipboard.SetText("hello 漢字");
        Assert.Equal("hello 漢字", clipboard.GetText());

        clipboard.SetText("replaced");
        Assert.Equal("replaced", clipboard.GetText());
    }

    private sealed class FakeInputApi : IInputApi
    {
        public List<InputEvent> Events { get; } = new();

        public void Send(IReadOnlyList<InputEvent> events)
        {
            Assert.NotNull(events);
            Events.AddRange(events);
        }
    }

    private sealed class FakeClipboard : IClipboard
    {
        private string? _text;

        public string? GetText() => _text;

        public void SetText(string text) => _text = text;
    }
}
