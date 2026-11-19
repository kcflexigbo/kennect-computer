using KennectComputer.Host.Protocol;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// An owned chat-like window for the M3 dry run (DESIGN §16): a conversation
/// list, a header, a read-only message pane, a composer, and an unnamed Send
/// button. The test owns, focuses, and closes it; no external app is launched.
/// </summary>
internal sealed class M3ChatTestWindow : IDisposable
{
    public const string WindowTitle = "Kennect M3 Test";
    public const string TestChat = "Test Chat";
    public const string OtherChat = "Other Chat";
    public const string NoChat = "No chat";
    public const string PreviousMessage = "Previous message from Alex";
    public const string ReplyText = "Kennect reply 汉字 123";

    private readonly OwnedWindowHost<M3ChatForm> _host;

    private M3ChatTestWindow(OwnedWindowHost<M3ChatForm> host)
    {
        _host = host;
    }

    public nint Handle => _host.Handle;

    /// <summary>The live outer window bounds in screen pixels.</summary>
    public Rect Bounds => _host.ReadBounds();

    public string ClassName => _host.ClassName;

    public string ProcessName => _host.ProcessName;

    public int Pid => _host.Pid;

    public bool IsAlive => _host.Form.IsHandleCreated && !_host.Form.IsDisposed;

    /// <summary>The header label's text, which is also its accessible name.</summary>
    public string HeaderText => _host.Invoke(() => _host.Form.HeaderLabel.Text);

    public string MessagesText => _host.Invoke(() => _host.Form.Messages.Text);

    public string ComposerText => _host.Invoke(() => _host.Form.Composer.Text);

    public int SendCount => _host.Invoke(() => _host.Form.SendCount);

    public static M3ChatTestWindow Start(bool duplicateTestChat = false)
        => new(OwnedWindowHost<M3ChatForm>.Start(
            () => new M3ChatForm(duplicateTestChat),
            "kennect-m3-chat-window",
            activate: true));

    /// <summary>
    /// A one-line environment description for focus and input failures, so a
    /// non-interactive desktop is reported instead of looking like a test bug.
    /// </summary>
    public string DescribeEnvironment()
    {
        var foreground = OwnedWindowNative.GetForegroundWindowHandle();
        return $"owned=0x{Handle:X}, visible={OwnedWindowNative.IsVisible(Handle)}, "
            + $"foreground=0x{foreground:X}, foregroundPid={OwnedWindowNative.ProcessIdOf(foreground)}, "
            + $"testPid={Pid}";
    }

    /// <summary>The live native identity the registry resolves and acts on.</summary>
    public RawWindow ToRawWindow() => new(Handle, Pid, WindowTitle, ProcessName, Bounds)
    {
        ClassName = ClassName,
        Visible = true,
        Minimized = false,
        Focused = OwnedWindowNative.GetForegroundWindowHandle() == Handle,
        ExStyle = 0,
    };

    public void Dispose() => _host.Dispose();
}

/// <summary>The chat-like control surface the M3 dry run drives through real input.</summary>
internal sealed class M3ChatForm : Form
{
    public M3ChatForm(bool duplicateTestChat)
    {
        Text = M3ChatTestWindow.WindowTitle;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(20, 20);
        ClientSize = new Size(560, 320);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        TestChatButton = new Button
        {
            AccessibleName = M3ChatTestWindow.TestChat,
            Text = M3ChatTestWindow.TestChat,
            Location = new Point(12, 12),
            Size = new Size(120, 30),
        };

        OtherChatButton = new Button
        {
            AccessibleName = M3ChatTestWindow.OtherChat,
            Text = M3ChatTestWindow.OtherChat,
            Location = new Point(12, 50),
            Size = new Size(120, 30),
        };

        // A second conversation with the same accessible name, at distinct
        // bounds, for the ambiguity test. Hidden unless requested so the other
        // tests see exactly one conversation per title.
        DuplicateTestChatButton = new Button
        {
            AccessibleName = M3ChatTestWindow.TestChat,
            Text = M3ChatTestWindow.TestChat,
            Location = new Point(12, 88),
            Size = new Size(120, 30),
            Visible = duplicateTestChat,
        };

        // No AccessibleName: the label's accessible name follows its text, so
        // the header can be asserted as role text with the conversation title.
        HeaderLabel = new Label
        {
            Text = M3ChatTestWindow.NoChat,
            AutoSize = true,
            Location = new Point(150, 12),
        };

        Messages = new TextBox
        {
            AccessibleName = "Messages",
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Text = string.Empty,
            Location = new Point(150, 46),
            Size = new Size(380, 180),
        };

        Composer = new TextBox
        {
            AccessibleName = "Composer",
            Multiline = true,
            Text = string.Empty,
            Location = new Point(150, 236),
            Size = new Size(300, 50),
        };

        // The Send control is deliberately unnamed, like Feishu's paper-plane
        // button. It is the only unnamed Button in the window.
        SendButton = new Button
        {
            AccessibleName = string.Empty,
            Text = string.Empty,
            Location = new Point(460, 236),
            Size = new Size(70, 50),
        };

        TestChatButton.Click += (_, _) =>
            OpenChat(M3ChatTestWindow.TestChat, M3ChatTestWindow.PreviousMessage);
        DuplicateTestChatButton.Click += (_, _) =>
            OpenChat(M3ChatTestWindow.TestChat, M3ChatTestWindow.PreviousMessage);
        OtherChatButton.Click += (_, _) => OpenChat(M3ChatTestWindow.OtherChat, messages: null);
        SendButton.Click += (_, _) => Send();

        Controls.AddRange(
        [
            TestChatButton,
            OtherChatButton,
            DuplicateTestChatButton,
            HeaderLabel,
            Messages,
            Composer,
            SendButton,
        ]);
    }

    public Button TestChatButton { get; }

    public Button OtherChatButton { get; }

    public Button DuplicateTestChatButton { get; }

    public Label HeaderLabel { get; }

    public TextBox Messages { get; }

    public TextBox Composer { get; }

    public Button SendButton { get; }

    public int SendCount { get; private set; }

    private void OpenChat(string title, string? messages)
    {
        HeaderLabel.Text = title;

        if (messages is not null)
        {
            Messages.Text = messages;
        }
    }

    private void Send()
    {
        var text = Composer.Text;
        if (text.Length == 0)
        {
            return;
        }

        Messages.Text = Messages.Text.Length == 0
            ? text
            : Messages.Text + Environment.NewLine + text;
        Composer.Text = string.Empty;
        SendCount++;
    }
}
