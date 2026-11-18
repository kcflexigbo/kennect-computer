using KennectComputer.Host.Actions;
using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// Pins key/chord parsing and injection (DESIGN §6.5): the parser accepts the
/// documented key names and chords and rejects bare modifiers, empty chords, and
/// unknown keys; a chord presses modifiers in order, the key down and up, then
/// releases the modifiers in reverse; and a failed expectation, focus refusal,
/// or malformed key never sends input. Fakes stand in for the foreground API,
/// input sender, and walker, so no desktop, window, or real input is touched.
/// </summary>
public sealed class KeyActionTests
{
    private static readonly Rect InitialBounds = new(100, 50, 3000, 2000);

    [Theory]
    [InlineData("Enter", 0x0D)]
    [InlineData("Tab", 0x09)]
    [InlineData("Escape", 0x1B)]
    [InlineData("F1", 0x70)]
    [InlineData("F12", 0x7B)]
    [InlineData("F24", 0x87)]
    [InlineData("Up", 0x26)]
    [InlineData("Down", 0x28)]
    [InlineData("Left", 0x25)]
    [InlineData("Right", 0x27)]
    [InlineData("Home", 0x24)]
    [InlineData("End", 0x23)]
    [InlineData("PageUp", 0x21)]
    [InlineData("PageDown", 0x22)]
    [InlineData("Delete", 0x2E)]
    [InlineData("Backspace", 0x08)]
    [InlineData("Space", 0x20)]
    [InlineData("A", 0x41)]
    [InlineData("z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("9", 0x39)]
    [InlineData("enter", 0x0D)]
    [InlineData("pageup", 0x21)]
    public void TheParserAcceptsTheDocumentedKeyNames(string value, int virtualKey)
    {
        Assert.True(KeyParser.TryParse(value, out var chord));

        Assert.Empty(chord.Modifiers);
        Assert.Equal((ushort)virtualKey, chord.Key);
    }

    [Theory]
    [InlineData("Ctrl+A", 0x41, 0x11, 0x00)]
    [InlineData("Control+A", 0x41, 0x11, 0x00)]
    [InlineData("ctrl+shift+s", 0x53, 0x11, 0x10)]
    [InlineData("Shift+Enter", 0x0D, 0x10, 0x00)]
    [InlineData("Alt+F4", 0x73, 0x12, 0x00)]
    [InlineData("Win+R", 0x52, 0x5B, 0x00)]
    [InlineData("Ctrl+Alt+Delete", 0x2E, 0x11, 0x12)]
    [InlineData(" Ctrl + A ", 0x41, 0x11, 0x00)]
    public void TheParserAcceptsChordsInTheirWrittenOrder(
        string value,
        int virtualKey,
        int firstModifier,
        int secondModifier)
    {
        Assert.True(KeyParser.TryParse(value, out var chord));

        ushort[] expectedModifiers = secondModifier == 0
            ? [(ushort)firstModifier]
            : [(ushort)firstModifier, (ushort)secondModifier];
        Assert.Equal(expectedModifiers, chord.Modifiers);
        Assert.Equal((ushort)virtualKey, chord.Key);
    }

    [Theory]
    [InlineData("NotAKey")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Control")]
    [InlineData("Shift")]
    [InlineData("Alt")]
    [InlineData("Win")]
    [InlineData("Ctrl+Shift")]
    [InlineData("+")]
    [InlineData("Ctrl++A")]
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("!")]
    public void TheParserRejectsEmptyChordsBareModifiersAndUnknownKeys(string value)
    {
        Assert.False(KeyParser.TryParse(value, out _));
    }

    [Fact]
    public async Task ControlASendsControlDownADownAUpControlUp()
    {
        var harness = Harness.Create();

        var result = await harness.Action.ExecuteAsync(
            new KeyRequest { Keys = "Ctrl+A" },
            CancellationToken.None);

        Assert.Equal(
            ["KeyDown:17", "KeyDown:65", "KeyUp:65", "KeyUp:17"],
            harness.Input.Calls);
        Assert.Equal("key", result.Action);
        Assert.Equal("keys", result.Method);
        Assert.True(result.NeedsObservation);
        Assert.Equal(harness.SelectedWindow.WindowId, result.Window.WindowId);
        Assert.Equal(1, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task ShiftEnterSendsShiftDownEnterDownEnterUpShiftUp()
    {
        var harness = Harness.Create();

        await harness.Action.ExecuteAsync(
            new KeyRequest { Keys = "Shift+Enter" },
            CancellationToken.None);

        Assert.Equal(
            ["KeyDown:16", "KeyDown:13", "KeyUp:13", "KeyUp:16"],
            harness.Input.Calls);
    }

    [Fact]
    public async Task AForegroundRefusalSendsNoKeyEvents()
    {
        var harness = Harness.Create(foregroundSucceeds: false);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new KeyRequest { Keys = "Ctrl+A" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.FocusRefused, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task AFailedExpectationSendsNoKeyEvents()
    {
        var walker = new FakeUiaWalker(new TreeModel { Role = "window", Name = "Notepad" });
        var harness = Harness.Create(walker: walker);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new KeyRequest
                {
                    Keys = "Ctrl+A",
                    Expect = [new ExpectNode { Name = "Missing" }],
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.ExpectationFailed, failure.Code);
        Assert.Equal("node:Missing", failure.FailedPredicate);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task AnUnknownKeyIsInvalidAndSendsNoInput()
    {
        var harness = Harness.Create();

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new KeyRequest { Keys = "Ctrl+NotAKey" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
        Assert.Equal(0, harness.Foreground.SetForegroundCalls);
    }

    [Fact]
    public async Task AChordFailureReleasesEveryModifierInReverseOrder()
    {
        var harness = Harness.Create();

        // The first release fails (S up); Shift and Ctrl must still be released,
        // in reverse press order, so no modifier is left down system-wide.
        var releaseFailed = false;
        harness.Input.BeforeKeyUp = () =>
        {
            if (!releaseFailed)
            {
                releaseFailed = true;
                throw new InvalidOperationException("send failed");
            }
        };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Action.ExecuteAsync(
                new KeyRequest { Keys = "Ctrl+Shift+S" },
                CancellationToken.None));

        Assert.Equal("send failed", failure.Message);
        Assert.Equal(
            ["KeyDown:17", "KeyDown:16", "KeyDown:83", "KeyUp:16", "KeyUp:17"],
            harness.Input.Calls);
    }

    [Fact]
    public async Task NoSelectedWindowIsInvalidAndSendsNoInput()
    {
        var harness = Harness.Create(select: false);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Action.ExecuteAsync(
                new KeyRequest { Keys = "Enter" },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Empty(harness.Input.Calls);
    }

    [Fact]
    public async Task HonorsCancellationBeforeInput()
    {
        var harness = Harness.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Action.ExecuteAsync(
                new KeyRequest { Keys = "Ctrl+A" },
                cancellation.Token));

        Assert.Empty(harness.Input.Calls);
    }

    /// <summary>
    /// One selected window plus the key dependencies, wired as the dispatcher
    /// will wire them. The focus timeout is short so refusal tests do not wait
    /// on the production one-second deadline.
    /// </summary>
    private sealed class Harness
    {
        private Harness(FakeUiaWalker? walker, bool foregroundSucceeds, bool select)
        {
            Raw = new RawWindow(1, 100, "Notepad", "Notepad", InitialBounds)
            {
                ClassName = "NotepadClass",
            };

            var windows = new FakeWindowApi(Raw);
            var registry = new WindowRegistry(windows);
            SelectedWindow = registry.Register(Raw);
            var selection = new SelectionStore();

            if (select)
            {
                selection.Select(SelectedWindow);
            }

            Input = new FakeInputSender();
            Foreground = new FakeWin32
            {
                InitialForeground = 99,
                ForegroundSucceeds = foregroundSucceeds,
            };
            Walker = walker
                ?? new FakeUiaWalker(new TreeModel { Role = "window", Name = "Notepad" });

            Action = new KeyAction(
                new ActionPreflight(
                    new ForegroundService(Foreground),
                    selection,
                    registry,
                    new ExpectEvaluator(Walker, Foreground),
                    TimeSpan.FromMilliseconds(10)),
                Input);
        }

        public RawWindow Raw { get; }

        public WindowInfo SelectedWindow { get; }

        public FakeInputSender Input { get; }

        public FakeWin32 Foreground { get; }

        public FakeUiaWalker Walker { get; }

        public KeyAction Action { get; }

        public static Harness Create(
            FakeUiaWalker? walker = null,
            bool foregroundSucceeds = true,
            bool select = true)
            => new(walker, foregroundSucceeds, select);
    }
}
