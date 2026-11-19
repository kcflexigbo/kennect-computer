using System.ComponentModel;

using KennectComputer.Host.Actions;
using KennectComputer.Host.Control;
using KennectComputer.Host.Input;
using KennectComputer.Host.Protocol;
using KennectComputer.Host.Uia;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// M2 input integration against an owned WinForms window (DESIGN §6, §21).
///
/// The real <see cref="WindowsInputSender"/>, <see cref="WindowsClipboard"/>,
/// <see cref="WindowsUiaWalker"/>, and <see cref="WindowsUiaNodeResolver"/> run
/// against <see cref="M2TestWindow"/>: an in-process window the test owns,
/// focuses, and closes. No external app is launched and no user window is
/// touched. The collection is non-parallel because there is one real mouse and
/// keyboard; two of these tests must never inject input at the same time.
/// </summary>
[Collection(M2InputCollection.Name)]
public sealed class M2InputTests
{
    private const string PasteText = "Kennect 汉字 123";
    private const string KeysText = "kennect 123";
    private const string BatchText = "batch text";

    /// <summary>
    /// Distinct text left on the clipboard before a paste. If the target reads
    /// the clipboard after the host restored it, the control receives this
    /// sentinel instead of the typed text and the test fails.
    /// </summary>
    private const string ClipboardSentinel = "SENTINEL user clipboard 请勿粘贴";

    [Fact]
    public async Task ClicksTheButtonByNodeAndTheLabelChanges()
    {
        using var window = M2TestWindow.Start();
        var harness = new ActionHarness(window.Handle, () => window.Bounds, window.ToRawWindow());
        var button = Find(harness.Snapshot(image: false), "button", "Action");

        var result = await InputAsync(
            window,
            () => harness.Click.ExecuteAsync(
                new ClickRequest { NodeId = button.NodeId },
                CancellationToken.None),
            "the node click");

        Assert.Equal("click", result.Action);
        Assert.Equal("pattern", result.Method);
        Assert.True(result.NeedsObservation);
        Assert.Equal(harness.SelectedWindow.WindowId, result.Window.WindowId);
        Assert.Equal("clicked", WaitForResult(window, "clicked"));
    }

    [Fact]
    public async Task ClicksTheButtonByImageSpaceCoordinatesFromARealSnapshot()
    {
        using var window = M2TestWindow.Start();
        var harness = new ActionHarness(window.Handle, () => window.Bounds, window.ToRawWindow());
        var snapshot = harness.Snapshot(image: true);
        var button = Find(snapshot, "button", "Action");

        // The image carries physical pixels per returned image pixel and the
        // physical bounds it was captured from; image coordinates are derived
        // from the node's physical bounds through exactly that mapping.
        var image = snapshot.Image
            ?? throw new InvalidOperationException(
                "M2 input integration needs an interactive desktop that allows window capture: "
                + "the owned test window produced no snapshot image, so an image-space click "
                + "cannot be exercised. No input was sent.");
        var imageX = ((button.Bounds.X + (button.Bounds.Width / 2.0)) - image.SourceBounds.X)
            / image.Scale;
        var imageY = ((button.Bounds.Y + (button.Bounds.Height / 2.0)) - image.SourceBounds.Y)
            / image.Scale;

        var result = await InputAsync(
            window,
            () => harness.Click.ExecuteAsync(
                new ClickRequest { X = imageX, Y = imageY },
                CancellationToken.None),
            "the image-space click");

        Assert.Equal("click", result.Action);
        Assert.Equal("coordinates", result.Method);
        Assert.Equal("clicked", WaitForResult(window, "clicked"));
    }

    [Fact]
    public async Task PasteEntersCjkTextAndReadTextReturnsItExactly()
    {
        using var window = M2TestWindow.Start();
        var harness = new ActionHarness(window.Handle, () => window.Bounds, window.ToRawWindow());
        var editor = Find(harness.Snapshot(image: false), "edit", "Editor");

        // A distinct sentinel makes the restore race observable: the host must
        // keep the typed text on the clipboard until the editor consumes the
        // paste, so the editor can only ever receive PasteText. The user's own
        // clipboard text is put back below.
        var saved = harness.Clipboard.GetText();
        try
        {
            harness.Clipboard.SetText(ClipboardSentinel);

            // The editor is slow to consume Ctrl+V: an immediate clipboard
            // restore would make it paste the sentinel.
            window.DelayNextPaste(TimeSpan.FromMilliseconds(150));

            var result = await InputAsync(
                window,
                () => harness.Type.ExecuteAsync(
                    new TypeRequest { Text = PasteText, NodeId = editor.NodeId },
                    CancellationToken.None),
                "the paste");

            Assert.Equal("type", result.Action);
            Assert.Equal("paste", result.Method);

            // ReadTextAction re-resolves the live node; the action's own wait
            // already observed the pasted text, so a single read is enough.
            var read = await harness.ReadText.ExecuteAsync(
                new ReadTextRequest { NodeId = editor.NodeId },
                CancellationToken.None);
            Assert.Equal(PasteText, read.Text);

            // The saved clipboard was restored after the target consumed it.
            Assert.Equal(ClipboardSentinel, harness.Clipboard.GetText());
        }
        finally
        {
            harness.Clipboard.SetText(saved ?? string.Empty);
        }
    }

    [Fact]
    public async Task KeysMethodTypesAsciiTextThatReadTextMatches()
    {
        using var window = M2TestWindow.Start();
        var harness = new ActionHarness(window.Handle, () => window.Bounds, window.ToRawWindow());
        var editor = Find(harness.Snapshot(image: false), "edit", "Editor");

        var result = await InputAsync(
            window,
            () => harness.Type.ExecuteAsync(
                new TypeRequest
                {
                    Text = KeysText,
                    Method = TypeMethod.Keys,
                    NodeId = editor.NodeId,
                },
                CancellationToken.None),
            "the keys typing");

        Assert.Equal("type", result.Action);
        Assert.Equal("keys", result.Method);
        Assert.Equal(KeysText, await ReadTextUntilAsync(harness, editor.NodeId, KeysText));
    }

    [Fact]
    public async Task PasswordFieldContentIsNeverCapturedOrReturned()
    {
        using var window = M2TestWindow.Start();
        var harness = new ActionHarness(window.Handle, () => window.Bounds, window.ToRawWindow());
        var snapshot = harness.Snapshot(image: false);
        var password = Find(snapshot, "edit", "Password");

        // The host-internal walker value is not observable directly; read_text
        // and find are the two paths that could expose it.
        var readFailure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.ReadText.ExecuteAsync(
                new ReadTextRequest { NodeId = password.NodeId },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, readFailure.Code);
        Assert.DoesNotContain(M2TestWindow.PasswordText, readFailure.Message);

        var found = await harness.Find.ExecuteAsync(
            new FindRequest { Text = M2TestWindow.PasswordText },
            CancellationToken.None);

        Assert.Empty(found.Matches);
    }

    [Fact]
    public async Task ScrollsTheListAndChangesItsTopIndex()
    {
        using var window = M2TestWindow.Start();
        var harness = new ActionHarness(window.Handle, () => window.Bounds, window.ToRawWindow());
        var list = Find(harness.Snapshot(image: false), "list", "List");

        Assert.Equal(0, window.ListTopIndex);

        // The wheel is delivered to the focused control; focusing the list here
        // keeps the assertion about the window's scroll state, not about the
        // machine's "scroll inactive windows" setting.
        window.FocusList();

        var result = await InputAsync(
            window,
            () => harness.Scroll.ExecuteAsync(
                new ScrollRequest
                {
                    NodeId = list.NodeId,
                    Direction = ScrollDirection.Down,
                    Amount = 3,
                },
                CancellationToken.None),
            "the scroll");

        Assert.Equal("scroll", result.Action);
        Assert.Equal("wheel", result.Method);
        Assert.True(
            WaitForTopIndexChange(window, initial: 0) > 0,
            "The list did not scroll; the wheel input did not reach the owned window.");
    }

    [Fact]
    public async Task ANodeFromAnOlderSnapshotReturnsStaleSnapshotAndDoesNotClick()
    {
        using var window = M2TestWindow.Start();
        var harness = new ActionHarness(window.Handle, () => window.Bounds, window.ToRawWindow());
        var first = harness.Snapshot(image: false);
        var button = Find(first, "button", "Action");

        // The second snapshot replaces the stored generation, so the first
        // node ID is now stale even though the live button still matches.
        harness.Snapshot(image: false);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Click.ExecuteAsync(
                new ClickRequest { NodeId = button.NodeId },
                CancellationToken.None));

        Assert.Equal(ErrorCode.StaleSnapshot, failure.Code);
        Settle();
        Assert.Equal("Result", window.ResultText);
    }

    [Fact]
    public async Task AFailedExpectationReturnsExpectationFailedAndDoesNotClick()
    {
        using var window = M2TestWindow.Start();
        var harness = new ActionHarness(window.Handle, () => window.Bounds, window.ToRawWindow());
        var button = Find(harness.Snapshot(image: false), "button", "Action");

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Click.ExecuteAsync(
                new ClickRequest
                {
                    NodeId = button.NodeId,
                    Expect = [new ExpectNode { Name = "Missing" }],
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.ExpectationFailed, failure.Code);
        Assert.Equal("node:Missing", failure.FailedPredicate);
        Settle();
        Assert.Equal("Result", window.ResultText);
    }

    [Fact]
    public async Task ABatchRunsClickTypeAndReadTextInOrder()
    {
        using var window = M2TestWindow.Start();
        var harness = new ActionHarness(window.Handle, () => window.Bounds, window.ToRawWindow());
        var snapshot = harness.Snapshot(image: false);
        var button = Find(snapshot, "button", "Action");
        var editor = Find(snapshot, "edit", "Editor");

        // The sentinel proves the paste waited for target consumption: the
        // read_text step immediately after type must already see the text,
        // before the host restores the sentinel. The user's clipboard text is
        // put back below.
        var saved = harness.Clipboard.GetText();
        try
        {
            harness.Clipboard.SetText(ClipboardSentinel);

            // The type step's paste is slow to be consumed, exactly as in the
            // standalone paste test.
            window.DelayNextPaste(TimeSpan.FromMilliseconds(150));

            var result = await InputAsync(
                window,
                () => harness.Batch.ExecuteAsync(
                    new BatchRequest
                    {
                        Steps =
                        [
                            new ClickStep
                            {
                                Request = new ClickRequest { NodeId = button.NodeId },
                            },
                            new TypeStep
                            {
                                Request = new TypeRequest
                                {
                                    Text = BatchText,
                                    NodeId = editor.NodeId,
                                },
                            },
                            new ReadTextStep
                            {
                                Request = new ReadTextRequest { NodeId = editor.NodeId },
                            },
                        ],
                    },
                    CancellationToken.None),
                "the batch");

            Assert.Null(result.FailedIndex);
            Assert.Equal(3, result.Steps.Count);
            Assert.Equal([0, 1, 2], result.Steps.Select(step => step.Index));
            Assert.Equal(["click", "type", "readText"], result.Steps.Select(step => step.Kind));
            Assert.All(result.Steps, step => Assert.True(step.Ok));

            Assert.Equal("click", Assert.IsType<ActionResult>(result.Steps[0].Data).Action);
            Assert.Equal("type", Assert.IsType<ActionResult>(result.Steps[1].Data).Action);
            var read = Assert.IsType<ReadTextResult>(result.Steps[2].Data);
            Assert.Equal(editor.NodeId, read.NodeId);

            // The type step only completes after the editor shows the text, so
            // the immediately following read is exact, not eventually exact.
            Assert.Equal(BatchText, read.Text);
            Assert.Equal(ClipboardSentinel, harness.Clipboard.GetText());
            Assert.Equal("clicked", WaitForResult(window, "clicked"));
        }
        finally
        {
            harness.Clipboard.SetText(saved ?? string.Empty);
        }
    }

    [Fact]
    public async Task ABatchWithAFailingMiddleStepReportsItsIndexAndStops()
    {
        using var window = M2TestWindow.Start();
        var harness = new ActionHarness(window.Handle, () => window.Bounds, window.ToRawWindow());
        var snapshot = harness.Snapshot(image: false);
        var editor = Find(snapshot, "edit", "Editor");
        var button = Find(snapshot, "button", "Action");

        var result = await harness.Batch.ExecuteAsync(
            new BatchRequest
            {
                Steps =
                [
                    new ReadTextStep
                    {
                        Request = new ReadTextRequest { NodeId = editor.NodeId },
                    },
                    new TypeStep
                    {
                        Request = new TypeRequest
                        {
                            Text = string.Empty,
                            NodeId = editor.NodeId,
                        },
                    },
                    new ClickStep
                    {
                        Request = new ClickRequest { NodeId = button.NodeId },
                    },
                ],
            },
            CancellationToken.None);

        Assert.Equal(1, result.FailedIndex);
        Assert.Equal(2, result.Steps.Count);

        Assert.Equal(0, result.Steps[0].Index);
        Assert.True(result.Steps[0].Ok);

        Assert.Equal(1, result.Steps[1].Index);
        Assert.False(result.Steps[1].Ok);
        Assert.Equal(ErrorCode.InvalidRequest, result.Steps[1].Code);
        Assert.Null(result.Steps[1].Data);

        // The third step would have clicked the button; it never ran.
        Settle();
        Assert.Equal("Result", window.ResultText);
    }

    private static Node Find(Snapshot snapshot, string role, string name)
        => Assert.Single(snapshot.Nodes, node => node.Role == role && node.Name == name);

    /// <summary>
    /// Runs a mutating action and turns a focus or input-injection failure into
    /// an explicit environment error. The integration must never pass silently
    /// when the desktop cannot accept real input.
    /// </summary>
    private static async Task<T> InputAsync<T>(
        M2TestWindow window,
        Func<Task<T>> execute,
        string operation)
    {
        try
        {
            return await execute();
        }
        catch (ActionFailedException failure) when (failure.Code == ErrorCode.FocusRefused)
        {
            throw new InvalidOperationException(
                "M2 input integration needs an interactive desktop that allows foreground "
                + $"activation: {operation} could not focus the owned test window, so no input "
                + $"was sent. {failure.Message} ({window.DescribeEnvironment()})",
                failure);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "M2 input integration needs an interactive desktop that allows input injection: "
                + $"{operation} could not send input to the owned test window. "
                + $"{exception.Message} ({window.DescribeEnvironment()})",
                exception);
        }
    }

    /// <summary>Polls the result label; UIA activation and input are asynchronous.</summary>
    private static string WaitForResult(M2TestWindow window, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var last = window.ResultText;

        while (DateTime.UtcNow < deadline && !string.Equals(last, expected, StringComparison.Ordinal))
        {
            Thread.Sleep(10);
            last = window.ResultText;
        }

        return last;
    }

    /// <summary>Polls the list until the wheel has moved its top index.</summary>
    private static int WaitForTopIndexChange(M2TestWindow window, int initial)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var last = window.ListTopIndex;

        while (DateTime.UtcNow < deadline && last == initial)
        {
            Thread.Sleep(10);
            last = window.ListTopIndex;
        }

        return last;
    }

    /// <summary>
    /// Repeats a read_text until the control shows the expected text. Typed
    /// input is queued to the app thread, so the first read can observe the
    /// pre-input state.
    /// </summary>
    private static async Task<string> ReadTextUntilAsync(
        ActionHarness harness,
        string nodeId,
        string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var last = string.Empty;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = (await harness.ReadText.ExecuteAsync(
                    new ReadTextRequest { NodeId = nodeId },
                    CancellationToken.None)).Text;

                if (string.Equals(last, expected, StringComparison.Ordinal))
                {
                    return last;
                }
            }
            catch (ActionFailedException)
            {
                // A transient provider read is retried until the deadline.
            }

            await Task.Delay(25);
        }

        return last;
    }

    /// <summary>Gives a would-be asynchronous click time to fire.</summary>
    private static void Settle() => Thread.Sleep(150);
}

/// <summary>
/// Serializes the M2 input integration with every other test collection: there
/// is one real mouse and keyboard, so no other window may be shown or focused
/// while these tests inject input.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class M2InputCollection
{
    public const string Name = "M2 input integration";
}
