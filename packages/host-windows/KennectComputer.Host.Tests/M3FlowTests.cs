using System.ComponentModel;

using KennectComputer.Host.Actions;
using KennectComputer.Host.Control;
using KennectComputer.Host.Protocol;

namespace KennectComputer.Host.Tests;

/// <summary>
/// M3 dry run of the Feishu reply choreography against an owned chat-like
/// window (DESIGN §16). The window is created, focused, and closed by the test;
/// no external app is launched and no real message is sent.
///
/// Fidelity limit: the conversation entries are Buttons, so the window-wide
/// `ExpectNode` for "Test Chat" sees only the header text. Live Feishu also
/// exposes the title as a text node in the chat list; the runbook's fresh
/// header read immediately before Send is the binding guard there. This dry
/// run does not model that duplication.
/// </summary>
[Collection(M2InputCollection.Name)]
public sealed class M3FlowTests
{
    [Fact]
    public async Task FullReplyFlowSendsExactlyTheComposedText()
    {
        using var window = M3ChatTestWindow.Start();
        var harness = Harness(window);

        var snapshot = harness.Snapshot(image: false);
        var testChat = Find(snapshot, "button", M3ChatTestWindow.TestChat);
        await InputAsync(
            window,
            () => harness.Click.ExecuteAsync(
                new ClickRequest { NodeId = testChat.NodeId },
                CancellationToken.None),
            "opening the test chat");

        Assert.Equal(
            M3ChatTestWindow.TestChat,
            WaitFor(() => window.HeaderText, M3ChatTestWindow.TestChat));

        snapshot = harness.Snapshot(image: false);
        var messages = Find(snapshot, "edit", "Messages");
        var before = await harness.ReadText.ExecuteAsync(
            new ReadTextRequest { NodeId = messages.NodeId },
            CancellationToken.None);
        Assert.Contains(M3ChatTestWindow.PreviousMessage, before.Text);

        snapshot = harness.Snapshot(image: false);
        var composer = Find(snapshot, "edit", "Composer");
        var send = Find(snapshot, "button", "");

        var typed = await InputAsync(
            window,
            () => harness.Type.ExecuteAsync(
                new TypeRequest
                {
                    Text = M3ChatTestWindow.ReplyText,
                    NodeId = composer.NodeId,
                },
                CancellationToken.None),
            "composing the reply");
        Assert.Equal("paste", typed.Method);

        var draft = await ReadTextUntilAsync(
            harness,
            composer.NodeId,
            M3ChatTestWindow.ReplyText);
        Assert.Equal(M3ChatTestWindow.ReplyText, draft);

        var sent = await InputAsync(
            window,
            () => harness.Click.ExecuteAsync(
                new ClickRequest
                {
                    NodeId = send.NodeId,
                    Expect =
                    [
                        new ExpectNode
                        {
                            Role = "text",
                            Name = M3ChatTestWindow.TestChat,
                        },
                    ],
                },
                CancellationToken.None),
            "sending the reply");
        Assert.Equal("click", sent.Action);

        var expectedPane = M3ChatTestWindow.PreviousMessage
            + Environment.NewLine
            + M3ChatTestWindow.ReplyText;
        Assert.Equal(expectedPane, WaitFor(() => window.MessagesText, expectedPane));
        Assert.Equal(string.Empty, WaitFor(() => window.ComposerText, string.Empty));
        Assert.Equal(1, window.SendCount);
    }

    [Fact]
    public async Task SendWithTheWrongHeaderSendsNothing()
    {
        using var window = M3ChatTestWindow.Start();
        var harness = Harness(window);

        var snapshot = harness.Snapshot(image: false);
        var otherChat = Find(snapshot, "button", M3ChatTestWindow.OtherChat);
        await InputAsync(
            window,
            () => harness.Click.ExecuteAsync(
                new ClickRequest { NodeId = otherChat.NodeId },
                CancellationToken.None),
            "opening the other chat");

        Assert.Equal(
            M3ChatTestWindow.OtherChat,
            WaitFor(() => window.HeaderText, M3ChatTestWindow.OtherChat));

        snapshot = harness.Snapshot(image: false);
        var composer = Find(snapshot, "edit", "Composer");
        var send = Find(snapshot, "button", "");

        await InputAsync(
            window,
            () => harness.Type.ExecuteAsync(
                new TypeRequest
                {
                    Text = M3ChatTestWindow.ReplyText,
                    NodeId = composer.NodeId,
                },
                CancellationToken.None),
            "composing the reply");

        Assert.Equal(
            M3ChatTestWindow.ReplyText,
            WaitFor(() => window.ComposerText, M3ChatTestWindow.ReplyText));

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Click.ExecuteAsync(
                new ClickRequest
                {
                    NodeId = send.NodeId,
                    Expect =
                    [
                        new ExpectNode
                        {
                            Role = "text",
                            Name = M3ChatTestWindow.TestChat,
                        },
                    ],
                },
                CancellationToken.None));

        Assert.Equal(ErrorCode.ExpectationFailed, failure.Code);
        Assert.Equal(0, window.SendCount);
        Assert.Equal(M3ChatTestWindow.ReplyText, window.ComposerText);
        Assert.Equal(string.Empty, window.MessagesText);
    }

    [Fact]
    public async Task AmbiguousChatTitlesDoNotClick()
    {
        using var window = M3ChatTestWindow.Start(duplicateTestChat: true);
        var harness = Harness(window);

        var failure = await Assert.ThrowsAsync<ActionFailedException>(() =>
            harness.Click.ExecuteAsync(
                new ClickRequest { Text = M3ChatTestWindow.TestChat },
                CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, failure.Code);
        Assert.Equal(M3ChatTestWindow.NoChat, window.HeaderText);
    }

    private static ActionHarness Harness(M3ChatTestWindow window)
        => new(window.Handle, () => window.Bounds, window.ToRawWindow());

    private static Node Find(Snapshot snapshot, string role, string name)
        => Assert.Single(snapshot.Nodes, node => node.Role == role && node.Name == name);

    /// <summary>
    /// Polls a UI property. UIA activation and input are asynchronous, so a
    /// click or paste handler can land after the action call returns.
    /// </summary>
    private static string WaitFor(Func<string> read, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var last = read();

        while (DateTime.UtcNow < deadline
            && !string.Equals(last, expected, StringComparison.Ordinal))
        {
            Thread.Sleep(25);
            last = read();
        }

        return last;
    }

    /// <summary>
    /// Repeats a read_text until the control shows the expected text. A paste
    /// is consumed on the target's UI thread, so the first read can observe
    /// the pre-paste state.
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

    /// <summary>
    /// Turns a focus or input-injection failure into an explicit environment
    /// error. The dry run must never pass silently when the desktop cannot
    /// accept real input.
    /// </summary>
    private static async Task<T> InputAsync<T>(
        M3ChatTestWindow window,
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
                "M3 flow integration needs an interactive desktop that allows foreground "
                + $"activation: {operation} could not focus the owned test window, so no input "
                + $"was sent. {failure.Message} ({window.DescribeEnvironment()})",
                failure);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "M3 flow integration needs an interactive desktop that allows input injection: "
                + $"{operation} could not send input to the owned test window. "
                + $"{exception.Message} ({window.DescribeEnvironment()})",
                exception);
        }
    }
}
