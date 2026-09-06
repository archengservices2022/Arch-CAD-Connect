using System.Windows.Forms;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// Collects the two EXPLICIT inputs a Get Latest needs: the root document
/// number the user wants (a bootstrap selection - never inferred from an open
/// file) and the local workspace directory to materialize into. It performs no
/// network work itself; the controller runs the operation and reports the
/// result.
///
/// The workspace directory is remembered (non-secret) in
/// <see cref="ConnectSettings.LastWorkspaceRoot"/> only as a convenience
/// default - the user confirms it every time and it is never treated as
/// authority for anything.
/// </summary>
internal sealed class GetLatestDialog : Form
{
    private readonly TextBox _documentNumber = new() { Width = 320 };
    private readonly TextBox _workspaceRoot = new() { Width = 260 };
    private readonly Button _browse = new() { Text = "Browse…", Width = 75 };
    private readonly Label _status = new() { AutoSize = false, Width = 340, Height = 30, ForeColor = System.Drawing.Color.Firebrick };
    private readonly Button _ok = new() { Text = "Get Latest", DialogResult = DialogResult.None, Width = 90 };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public GetLatestDialog()
    {
        Text = "Get Latest from Arch PLM";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        AcceptButton = _ok;
        CancelButton = _cancel;

        _workspaceRoot.Text = ConnectSettings.Load().LastWorkspaceRoot ?? string.Empty;

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
            layout.Controls.Add(control);
        }

        Row("Document number", _documentNumber);

        var folderRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0) };
        folderRow.Controls.Add(_workspaceRoot);
        folderRow.Controls.Add(_browse);
        Row("Workspace folder", folderRow);

        layout.Controls.Add(new Label { Width = 1 });
        layout.Controls.Add(_status);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_ok);

        var root = new TableLayoutPanel { RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(layout);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _browse.Click += OnBrowse;
        _ok.Click += OnOk;
    }

    /// <summary>The document number the user typed (trimmed). Valid only when
    ///  <see cref="Form.ShowDialog()"/> returned <see cref="DialogResult.OK"/>.</summary>
    public string RootDocumentNumber { get; private set; } = string.Empty;

    /// <summary>The absolute workspace directory the user chose (trimmed).</summary>
    public string WorkspaceRoot { get; private set; } = string.Empty;

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose a local folder for the managed workspace",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (!string.IsNullOrWhiteSpace(_workspaceRoot.Text) && Directory.Exists(_workspaceRoot.Text))
        {
            picker.SelectedPath = _workspaceRoot.Text.Trim();
        }
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            _workspaceRoot.Text = picker.SelectedPath;
        }
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var number = _documentNumber.Text.Trim();
        var folder = _workspaceRoot.Text.Trim();

        if (number.Length == 0)
        {
            _status.Text = "Enter the document number of the assembly or part to fetch.";
            return;
        }
        if (folder.Length == 0)
        {
            _status.Text = "Choose a local workspace folder.";
            return;
        }
        if (!Path.IsPathFullyQualified(folder))
        {
            _status.Text = "The workspace folder must be a full path (e.g. C:\\Work\\ProjectX).";
            return;
        }

        RootDocumentNumber = number;
        WorkspaceRoot = folder;

        // Remember the folder (non-secret) for next time, keeping other prefs.
        (ConnectSettings.Load() with { LastWorkspaceRoot = folder }).Save();

        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _documentNumber.Dispose();
            _workspaceRoot.Dispose();
            _browse.Dispose();
        }
        base.Dispose(disposing);
    }
}
