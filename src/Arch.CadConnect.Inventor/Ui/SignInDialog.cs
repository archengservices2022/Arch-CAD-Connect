using System.Windows.Forms;

using Arch.CadConnect.Api;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// Modal credential prompt. Runs the actual sign-in inside its own message
/// loop (so <c>await</c> marshals back here correctly) and reports the outcome
/// via <see cref="DialogResult"/>. The password lives only in the masked
/// textbox for the lifetime of the dialog and is passed straight to
/// <see cref="ArchConnectionManager.SignInAsync"/>; it is never stored, logged
/// or copied.
///
/// There is NO "allow insecure transport" control: the transport policy
/// (<c>ArchServerUri</c>) permits plain http only for a loopback address and
/// cannot be loosened from the UI.
/// </summary>
internal sealed class SignInDialog : Form
{
    private readonly ArchConnectionManager _manager;

    private readonly TextBox _server = new() { Width = 320 };
    private readonly TextBox _email = new() { Width = 320 };
    private readonly TextBox _password = new() { Width = 320, UseSystemPasswordChar = true };
    private readonly Label _status = new() { AutoSize = false, Width = 320, Height = 32, ForeColor = System.Drawing.Color.Firebrick };
    private readonly Button _signIn = new() { Text = "Sign In", DialogResult = DialogResult.None, Width = 90 };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public SignInDialog(ArchConnectionManager manager)
    {
        _manager = manager;

        Text = "Sign in to Arch PLM";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        AcceptButton = _signIn;
        CancelButton = _cancel;

        var settings = ConnectSettings.Load();
        _server.Text = settings.ServerAddress;
        _email.Text = settings.LastEmail ?? string.Empty;

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
            layout.Controls.Add(control);
        }
        Row("Server", _server);
        Row("Email", _email);
        Row("Password", _password);
        layout.Controls.Add(new Label { Width = 1 });
        layout.Controls.Add(_status);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_signIn);

        var root = new TableLayoutPanel { RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(layout);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _signIn.Click += OnSignInClicked;
    }

    private async void OnSignInClicked(object? sender, EventArgs e)
    {
        SetBusy(true);
        _status.Text = string.Empty;

        // Remember the non-secret preferences (address + email).
        new ConnectSettings
        {
            ServerAddress = _server.Text.Trim(),
            LastEmail = _email.Text.Trim(),
        }.Save();

        try
        {
            await _manager.SignInAsync(
                _server.Text.Trim(),
                _email.Text.Trim(),
                _password.Text,
                clientLabel: DefaultClientLabel(),
                ct: CancellationToken.None);

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Core.Session.ArchServerUriException ex)
        {
            _status.Text = ex.Message;
        }
        catch (ArchApiException ex)
        {
            // ex.Message is one of a small set of safe strings by contract.
            _status.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _signIn.Enabled = !busy;
        _server.Enabled = _email.Enabled = _password.Enabled = !busy;
        _signIn.Text = busy ? "Signing in…" : "Sign In";
    }

    private static string DefaultClientLabel()
    {
        try
        {
            return $"Inventor add-in on {Environment.MachineName}";
        }
        catch
        {
            return "Inventor add-in";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _server.Dispose();
            _email.Dispose();
            _password.Dispose();
        }
        base.Dispose(disposing);
    }
}
