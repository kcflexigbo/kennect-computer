using System.Drawing;
using System.Windows.Forms;

namespace KennectComputer.Host.Policy;

/// <summary>
/// Asks the user whether an app may be controlled. The real implementation is a
/// WinForms dialog marshaled to the STA tray thread; unit tests substitute a
/// fake, so no dialog is ever shown in the automated suites (R28).
/// </summary>
public interface IAuthorizationPrompt
{
    Task<AuthorizationDecision> AskAsync(
        string processPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Fail-closed prompt used when the tray UI could not start. Status, window
/// listing, and snapshots still work; control of an app is denied.
/// </summary>
public sealed class DenyAllAuthorizationPrompt : IAuthorizationPrompt
{
    public Task<AuthorizationDecision> AskAsync(
        string processPath,
        CancellationToken cancellationToken)
        => Task.FromResult(AuthorizationDecision.Deny);
}

/// <summary>
/// The dialog surface behind an internal seam, so cancellation can close the
/// prompt without a real window in the automated suites.
/// </summary>
internal interface IAuthorizationDialogView : IDisposable
{
    /// <summary>Shows the modal on the UI thread and returns the choice.</summary>
    AuthorizationDecision Ask();

    /// <summary>Closes the dialog from another thread; safe after disposal.</summary>
    void Close();
}

/// <summary>
/// The real per-app prompt. The dialog itself is only ever created inside
/// <c>ShowDialog</c> on the STA thread; the marshaling function is injected so
/// the policy has no WinForms affinity of its own.
///
/// Cancellation (the stop hotkey or tray Stop) abandons the wait and closes the
/// modal. Leaving it open would make the host's own dialog a normal titled
/// window, which is exactly what DESIGN §8 hard rule 3 forbids.
/// </summary>
public sealed class WinFormsAuthorizationPrompt : IAuthorizationPrompt
{
    private readonly Func<Func<AuthorizationDecision>, Task<AuthorizationDecision>> _invokeOnUiThread;
    private readonly Func<string, IAuthorizationDialogView> _createDialog;

    public WinFormsAuthorizationPrompt(
        Func<Func<AuthorizationDecision>, Task<AuthorizationDecision>> invokeOnUiThread)
        : this(invokeOnUiThread, processPath => new AuthorizationDialog(processPath))
    {
    }

    internal WinFormsAuthorizationPrompt(
        Func<Func<AuthorizationDecision>, Task<AuthorizationDecision>> invokeOnUiThread,
        Func<string, IAuthorizationDialogView> createDialog)
    {
        _invokeOnUiThread = invokeOnUiThread
            ?? throw new ArgumentNullException(nameof(invokeOnUiThread));
        _createDialog = createDialog ?? throw new ArgumentNullException(nameof(createDialog));
    }

    public async Task<AuthorizationDecision> AskAsync(
        string processPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IAuthorizationDialogView? view = null;

        var task = _invokeOnUiThread(() =>
        {
            // A stop that landed before the tray thread ran the work must not
            // leave a modal behind with nobody waiting for it.
            if (cancellationToken.IsCancellationRequested)
            {
                return AuthorizationDecision.Deny;
            }

            var dialog = _createDialog(processPath);
            Volatile.Write(ref view, dialog);
            try
            {
                return dialog.Ask();
            }
            finally
            {
                dialog.Dispose();
            }
        });

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CloseDialog(Volatile.Read(ref view));
            throw;
        }
    }

    private static void CloseDialog(IAuthorizationDialogView? view)
    {
        if (view is null)
        {
            return;
        }

        try
        {
            view.Close();
        }
        catch (InvalidOperationException)
        {
            // The dialog was already disposed by the UI-thread finally.
        }
    }

    /// <summary>Allow once / Allow for session / Deny; closing means Deny.</summary>
    private sealed class AuthorizationDialog : Form, IAuthorizationDialogView
    {
        public AuthorizationDialog(string processPath)
        {
            Text = "Kennect Computer";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 190);

            var appName = Path.GetFileName(processPath);

            var heading = new Label
            {
                Text = $"Allow Kennect Computer to control {appName}?",
                Dock = DockStyle.Top,
                Height = 46,
                Padding = new Padding(14, 12, 14, 0),
                Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            };

            var path = new Label
            {
                Text = processPath,
                Dock = DockStyle.Top,
                Height = 48,
                Padding = new Padding(14, 4, 14, 0),
                AutoEllipsis = true,
                ForeColor = SystemColors.GrayText,
            };

            var allowOnce = new Button { Text = "Allow once", AutoSize = true };
            allowOnce.Click += (_, _) => Choose(AuthorizationDecision.AllowOnce);

            var allowSession = new Button { Text = "Allow for session", AutoSize = true };
            allowSession.Click += (_, _) => Choose(AuthorizationDecision.AllowSession);

            var deny = new Button { Text = "Deny", AutoSize = true };
            deny.Click += (_, _) => Choose(AuthorizationDecision.Deny);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 46,
                Padding = new Padding(10),
            };
            buttons.Controls.Add(deny);
            buttons.Controls.Add(allowSession);
            buttons.Controls.Add(allowOnce);

            AcceptButton = allowOnce;
            CancelButton = deny;
            Controls.Add(buttons);
            Controls.Add(path);
            Controls.Add(heading);
        }

        public AuthorizationDecision Decision { get; private set; } = AuthorizationDecision.Deny;

        public AuthorizationDecision Ask()
        {
            ShowDialog();
            return Decision;
        }

        void IAuthorizationDialogView.Close()
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    // Close must run on the thread that created the window;
                    // the stop hotkey calls this from a pool thread.
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (!IsDisposed)
                            {
                                Close();
                            }
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }));
                }
            }
            catch (InvalidOperationException)
            {
                // The dialog was disposed before the close was posted.
            }
        }

        private void Choose(AuthorizationDecision decision)
        {
            Decision = decision;
            Close();
        }
    }
}
