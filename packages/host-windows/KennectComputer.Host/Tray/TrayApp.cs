using System.Drawing;
using System.Threading;
using System.Windows.Forms;

using KennectComputer.Host.Control;
using KennectComputer.Host.Input;

namespace KennectComputer.Host.Tray;

/// <summary>
/// The Windows tray shell: Status, Stop, and Exit, plus the hidden window that
/// receives <c>WM_HOTKEY</c> and marshals UI work onto the STA thread.
///
/// Construct and run this only on the dedicated STA thread with a WinForms
/// message loop (R30). Unit tests never instantiate it; there is no tray icon,
/// dialog, or hotkey in the automated suites (R28).
/// </summary>
public sealed class TrayApp : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int BalloonTimeoutMs = 4000;

    private readonly ControlSession _control;
    private readonly StopHotkey _hotkey;
    private readonly Action _exit;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly MessageWindow _messageWindow;
    private readonly RegisteredWaitHandle? _showTrayWait;
    private bool _disposed;

    public TrayApp(
        ControlSession control,
        StopHotkey hotkey,
        EventWaitHandle? showTraySignal,
        Action exit)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        _messageWindow = new MessageWindow(hotkey);
        _ = _messageWindow.Handle; // Force handle creation on this STA thread.

        _hotkey.Register(_messageWindow.Handle, StopHotkey.DefaultId);

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Status", null, (_, _) => ShowStatus());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Stop", null, (_, _) => _control.Stop());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => RequestExit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = TooltipText(),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowStatus();

        _control.StateChanged += OnControlStateChanged;

        if (showTraySignal is not null)
        {
            // A second launch signals this event so the running host surfaces
            // its tray instead of starting a second pipe server.
            _showTrayWait = ThreadPool.RegisterWaitForSingleObject(
                showTraySignal,
                (_, _) => Post(() => ShowBalloon("Kennect Computer is already running.")),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }
    }

    /// <summary>Runs the tray message loop until <see cref="RequestExit"/>.</summary>
    public void Run()
    {
        UpdateTooltip();
        Application.Run();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the tray STA thread and completes with
    /// its result. Used by the per-app authorization dialog.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var posted = Post(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        if (!posted)
        {
            completion.TrySetException(
                new InvalidOperationException("The Kennect Computer tray UI is not running."));
        }

        return completion.Task;
    }

    /// <summary>Posts thread exit to the tray loop; safe from any thread.</summary>
    public void RequestExit() => Post(Application.ExitThread);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _control.StateChanged -= OnControlStateChanged;
        _showTrayWait?.Unregister(null);
        _hotkey.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _messageWindow.Dispose();
    }

    private void OnControlStateChanged(object? sender, EventArgs e) => Post(UpdateTooltip);

    private bool Post(Action action)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            _messageWindow.BeginInvoke(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The window handle is already gone (ObjectDisposedException is an
            // InvalidOperationException); the loop is shutting down.
            return false;
        }
    }

    private void UpdateTooltip()
    {
        if (_disposed)
        {
            return;
        }

        _icon.Text = TooltipText();
    }

    private string TooltipText()
    {
        var state = _control.IsHeld ? "session active" : "idle";
        return _hotkey.IsRegistered
            ? $"Kennect Computer - {state}"
            : $"Kennect Computer - {state} (hotkey unavailable)";
    }

    private void ShowStatus()
    {
        var session = _control.IsHeld
            ? "A control session is active."
            : "No control session is active.";
        var hotkey = _hotkey.IsRegistered
            ? $"Stop hotkey {_hotkey.Shortcut} is registered."
            : $"Stop hotkey {_hotkey.Shortcut} is unavailable.";

        ShowBalloon($"{session} {hotkey}");
    }

    private void ShowBalloon(string message)
    {
        if (_disposed)
        {
            return;
        }

        _icon.BalloonTipTitle = "Kennect Computer";
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(BalloonTimeoutMs);
    }

    /// <summary>
    /// The hidden top-level window that owns the hotkey registration. It is
    /// never shown; its only job is to receive <c>WM_HOTKEY</c> on the STA
    /// thread and to marshal posted UI work.
    /// </summary>
    private sealed class MessageWindow : Form
    {
        private readonly StopHotkey _hotkey;

        public MessageWindow(StopHotkey hotkey)
        {
            _hotkey = hotkey;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey && _hotkey.HandleMessage(message.WParam.ToInt32()))
            {
                // The stop key is consumed; it must not reach other handlers.
                return;
            }

            base.WndProc(ref message);
        }
    }
}
