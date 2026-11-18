using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using KennectComputer.Host.Protocol;
using KennectComputer.Host.Windows;

namespace KennectComputer.Host.Tests;

/// <summary>
/// The owned-window lifecycle shared by real-UIA tests (R3/R29): a form created
/// on an STA thread, shown once, and closed and joined deterministically.
///
/// The window is always owned by the test process. Tests must never launch an
/// external app or touch a user window; <c>activate</c> controls whether the
/// window takes the foreground, which only the input integration needs. The
/// handle, outer bounds, native class name, and process identity are captured
/// when the form first shows. <see cref="ReadBounds"/> re-reads the live form so
/// an action path can verify the window is still where the snapshot saw it.
/// </summary>
internal sealed class OwnedWindowHost<TForm> : IDisposable
    where TForm : Form
{
    private readonly Thread _thread;

    private OwnedWindowHost(
        TForm form,
        Thread thread,
        nint handle,
        Rect bounds,
        string className,
        string processName)
    {
        Form = form;
        _thread = thread;
        Handle = handle;
        Bounds = bounds;
        ClassName = className;
        ProcessName = processName;
        Pid = Environment.ProcessId;
    }

    public TForm Form { get; }

    public nint Handle { get; }

    /// <summary>The window's outer bounds in screen pixels when it first showed.</summary>
    public Rect Bounds { get; }

    public string ClassName { get; }

    public string ProcessName { get; }

    public int Pid { get; }

    public static OwnedWindowHost<TForm> Start(
        Func<TForm> create,
        string threadName,
        bool activate)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentException.ThrowIfNullOrEmpty(threadName);

        var ready = new ManualResetEventSlim(initialState: false);
        TForm? form = null;
        nint handle = 0;
        var bounds = new Rect();
        var className = string.Empty;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                form = create();

                form.Shown += (_, _) =>
                {
                    try
                    {
                        handle = form.Handle;
                        bounds = ToRect(form.Bounds);
                        className = ReadClassName(handle);

                        if (activate)
                        {
                            // The input integration needs a foreground window;
                            // the M1 snapshot harness deliberately does not take
                            // the user's foreground. Windows refuses a plain
                            // SetForegroundWindow after the process loses its
                            // startup foreground grace, so attach to the current
                            // foreground thread and verify, retrying briefly.
                            form.Activate();
                            for (var attempt = 0;
                                attempt < 5 && !OwnedWindowNative.TryTakeForeground(form.Handle);
                                attempt++)
                            {
                                Thread.Sleep(20);
                            }
                        }
                    }
                    finally
                    {
                        ready.Set();
                    }
                };

                Application.Run(form);
            }
            catch (Exception exception)
            {
                failure = exception;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = threadName,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException($"The owned test window ({threadName}) did not appear.");
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(
                $"The owned test window ({threadName}) failed to start.",
                failure);
        }

        if (form is null || handle == 0)
        {
            throw new InvalidOperationException(
                $"The owned test window ({threadName}) did not create a window handle.");
        }

        return new OwnedWindowHost<TForm>(
            form,
            thread,
            handle,
            bounds,
            className,
            Process.GetCurrentProcess().ProcessName);
    }

    /// <summary>
    /// Re-reads the live outer bounds on the form's thread. The test window does
    /// not move, but a moving/resizing window must be observed so coordinate
    /// mapping can reject a stale snapshot instead of clicking the wrong place.
    /// </summary>
    public Rect ReadBounds()
        => Invoke(() => ToRect(Form.Bounds));

    public T Invoke<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return (T)Form.Invoke(read)!;
    }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Form.Invoke(action);
    }

    public void Dispose()
    {
        try
        {
            if (Form.IsHandleCreated && !Form.IsDisposed)
            {
                Form.Invoke(new Action(Form.Close));
            }
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException derives from this; either way the message
            // loop already ended and there is nothing to close.
        }

        _thread.Join(TimeSpan.FromSeconds(10));
    }

    private static Rect ToRect(System.Drawing.Rectangle bounds)
        => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private static string ReadClassName(nint hwnd)
        => OwnedWindowNative.ReadClassName(hwnd);
}

/// <summary>
/// The native entry points the owned-window harness needs. They live in a
/// non-generic type because a <c>DllImport</c> method cannot be declared inside
/// a generic type.
/// </summary>
internal static class OwnedWindowNative
{
    private const int MaxClassNameLength = 256;

    public static string ReadClassName(nint hwnd)
    {
        var buffer = new StringBuilder(MaxClassNameLength);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    /// <summary>
    /// Takes the foreground with the attach/bring/focus recipe and verifies the
    /// result. Must be called from the window's own thread so SetActiveWindow
    /// and SetFocus target that thread's input queue.
    /// </summary>
    public static bool TryTakeForeground(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        if (GetForegroundWindow() == hwnd)
        {
            return true;
        }

        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == 0
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0
            && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, attach: true);

        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
            SetFocus(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, attach: false);
            }
        }

        return GetForegroundWindow() == hwnd;
    }

    public static nint GetForegroundWindowHandle() => GetForegroundWindow();

    public static uint ProcessIdOf(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public static bool IsVisible(nint hwnd) => hwnd != 0 && IsWindowVisible(hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);
}

/// <summary>
/// An owned WinForms window built for M2 input integration (DESIGN §21): a
/// focusable button whose click updates a label, an editor for paste and keys,
/// and a scrollable list. Unlike the M1 snapshot harness it takes the
/// foreground, because window-foreground verification is part of the input
/// contract. Controls set <c>AccessibleName</c> so UIA names are deterministic.
/// </summary>
internal sealed class M2TestWindow : IDisposable
{
    public const string WindowTitle = "Kennect M2 Test";

    /// <summary>A password value the tests assert is never captured or returned.</summary>
    public const string PasswordText = "KennectSecret123";

    private readonly OwnedWindowHost<M2TestForm> _host;

    private M2TestWindow(OwnedWindowHost<M2TestForm> host)
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

    /// <summary>The result label's current text: "Result" until the button clicks.</summary>
    public string ResultText => _host.Invoke(() => _host.Form.ResultLabel.Text);

    public int ListTopIndex => _host.Invoke(() => _host.Form.Items.TopIndex);

    public static M2TestWindow Start()
        => new(OwnedWindowHost<M2TestForm>.Start(
            () => new M2TestForm(),
            "kennect-m2-test-window",
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

    /// <summary>
    /// Makes the editor slow to process the next paste: it blocks its UI thread
    /// after the Ctrl+V key reaches it and before the edit control reads the
    /// clipboard. A host that restores the clipboard immediately makes the
    /// editor paste the restored sentinel; a host that waits for consumption
    /// keeps the typed text available. This turns a timing race into a
    /// deterministic assertion.
    /// </summary>
    public void DelayNextPaste(TimeSpan delay)
        => _host.Invoke(() => _host.Form.Editor.NextPasteDelay = delay);

    /// <summary>
    /// Focuses the list without touching its scroll position, so the wheel test
    /// observes list scrolling rather than hover-scroll settings.
    /// </summary>
    public void FocusList()
        => _host.Invoke(() =>
        {
            _host.Form.Items.Focus();
        });

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

/// <summary>The control surface the M2 integration drives through real input.</summary>
internal sealed class M2TestForm : Form
{
    public M2TestForm()
    {
        Text = M2TestWindow.WindowTitle;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(20, 20);
        ClientSize = new Size(420, 300);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        ActionButton = new Button
        {
            AccessibleName = "Action",
            Text = "Action",
            Location = new Point(20, 20),
            Size = new Size(140, 32),
        };

        ResultLabel = new Label
        {
            AccessibleName = "Result",
            Text = "Result",
            AutoSize = true,
            Location = new Point(180, 28),
        };

        Editor = new SlowPasteTextBox
        {
            AccessibleName = "Editor",
            Text = string.Empty,
            Location = new Point(20, 70),
            Size = new Size(300, 24),
        };

        Password = new TextBox
        {
            AccessibleName = "Password",
            Text = M2TestWindow.PasswordText,
            UseSystemPasswordChar = true,
            Location = new Point(20, 100),
            Size = new Size(300, 24),
        };

        Items = new ListBox
        {
            AccessibleName = "List",
            Location = new Point(20, 135),
            Size = new Size(300, 150),
            IntegralHeight = false,
        };

        for (var index = 0; index < 50; index++)
        {
            Items.Items.Add($"Item {index:00}");
        }

        ActionButton.Click += (_, _) => ResultLabel.Text = "clicked";

        Controls.AddRange([ActionButton, ResultLabel, Editor, Password, Items]);
    }

    public Button ActionButton { get; }

    public Label ResultLabel { get; }

    public SlowPasteTextBox Editor { get; }

    public TextBox Password { get; }

    public ListBox Items { get; }
}

/// <summary>
/// An editor whose next Ctrl+V processing can be delayed, modeling a slow
/// target. The delay runs on the UI thread before the edit control reads the
/// clipboard, which makes a clipboard-restore race observable instead of
/// intermittent.
/// </summary>
internal sealed class SlowPasteTextBox : TextBox
{
    private const int WmKeyDown = 0x0100;
    private const int VkPaste = 0x56;

    /// <summary>One-shot delay applied when the next V key-down reaches the control.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal TimeSpan NextPasteDelay { get; set; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmKeyDown
            && m.WParam == (nint)VkPaste
            && NextPasteDelay > TimeSpan.Zero)
        {
            var delay = NextPasteDelay;
            NextPasteDelay = TimeSpan.Zero;
            Thread.Sleep(delay);
        }

        base.WndProc(ref m);
    }
}
