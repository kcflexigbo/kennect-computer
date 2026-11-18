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
/// The real per-app prompt. The dialog itself is only ever created inside
/// <c>ShowDialog</c> on the STA thread; the marshaling function is injected so
/// the policy has no WinForms affinity of its own.
/// </summary>
public sealed class WinFormsAuthorizationPrompt : IAuthorizationPrompt
{
    private readonly Func<Func<AuthorizationDecision>, Task<AuthorizationDecision>> _invokeOnUiThread;

    public WinFormsAuthorizationPrompt(
        Func<Func<AuthorizationDecision>, Task<AuthorizationDecision>> invokeOnUiThread)
        => _invokeOnUiThread = invokeOnUiThread
            ?? throw new ArgumentNullException(nameof(invokeOnUiThread));

    public Task<AuthorizationDecision> AskAsync(
        string processPath,
        CancellationToken cancellationToken)
        => _invokeOnUiThread(() => ShowDialog(processPath)).WaitAsync(cancellationToken);

    private static AuthorizationDecision ShowDialog(string processPath)
    {
        using var dialog = new AuthorizationDialog(processPath);
        dialog.ShowDialog();
        return dialog.Decision;
    }

    /// <summary>Allow once / Allow for session / Deny; closing means Deny.</summary>
    private sealed class AuthorizationDialog : Form
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

        private void Choose(AuthorizationDecision decision)
        {
            Decision = decision;
            Close();
        }
    }
}
