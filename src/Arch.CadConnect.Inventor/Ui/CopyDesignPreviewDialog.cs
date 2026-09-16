using System.Windows.Forms;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// P6A: collects the EXPLICIT inputs a Copy Design preview needs - the exact
/// search/replace token for the naming rule, and the destination folder the
/// proposed destinations should be validated/previewed against. It performs
/// no scanning, no planning, and no filesystem write itself; the controller
/// runs <c>CopyDesignPlanner.Plan</c> and shows the result. Destination
/// token/folder are never remembered as authority for anything - the engineer
/// confirms them every time, exactly like <see cref="GetLatestDialog"/>'s
/// workspace folder.
/// </summary>
internal sealed class CopyDesignPreviewDialog : Form
{
    private readonly TextBox _sourceToken = new() { Width = 140 };
    private readonly TextBox _destinationToken = new() { Width = 140 };
    private readonly TextBox _destinationFolder = new() { Width = 260 };
    private readonly Button _browse = new() { Text = "Browse…", Width = 75 };
    private readonly Label _status = new() { AutoSize = false, Width = 400, Height = 30, ForeColor = System.Drawing.Color.Firebrick };
    private readonly Button _ok = new() { Text = "Preview", DialogResult = DialogResult.None, Width = 90 };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public CopyDesignPreviewDialog()
    {
        Text = "Copy Design Preview (read-only)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        AcceptButton = _ok;
        CancelButton = _cancel;

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
            layout.Controls.Add(control);
        }

        Row("Source token (e.g. 10073)", _sourceToken);
        Row("Destination token (e.g. 10137)", _destinationToken);

        var folderRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0) };
        folderRow.Controls.Add(_destinationFolder);
        folderRow.Controls.Add(_browse);
        Row("Destination folder", folderRow);

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

    public string SourceToken { get; private set; } = string.Empty;
    public string DestinationToken { get; private set; } = string.Empty;
    public string DestinationFolder { get; private set; } = string.Empty;

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the destination folder to preview Copy Design destinations against",
            UseDescriptionForTitle = true,
            // BLOCKER 4 (P6A Round 3): P6A is PLAN + PREVIEW ONLY - zero
            // filesystem mutation. "New Folder" would let this read-only
            // preview dialog create a real directory; it must stay off.
            ShowNewFolderButton = false,
        };
        if (!string.IsNullOrWhiteSpace(_destinationFolder.Text) && Directory.Exists(_destinationFolder.Text))
        {
            picker.SelectedPath = _destinationFolder.Text.Trim();
        }
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            _destinationFolder.Text = picker.SelectedPath;
        }
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var source = _sourceToken.Text.Trim();
        var destination = _destinationToken.Text.Trim();
        var folder = _destinationFolder.Text.Trim();

        if (source.Length == 0)
        {
            _status.Text = "Enter the source token to search for (e.g. a project number).";
            return;
        }
        if (destination.Length == 0)
        {
            _status.Text = "Enter the destination token to replace it with.";
            return;
        }
        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            _status.Text = "The source and destination tokens must be different.";
            return;
        }
        if (folder.Length == 0)
        {
            _status.Text = "Choose a destination folder to preview against.";
            return;
        }
        if (!Path.IsPathFullyQualified(folder))
        {
            _status.Text = "The destination folder must be a full path (e.g. C:\\Work\\ProjectY).";
            return;
        }

        SourceToken = source;
        DestinationToken = destination;
        DestinationFolder = folder;

        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sourceToken.Dispose();
            _destinationToken.Dispose();
            _destinationFolder.Dispose();
            _browse.Dispose();
            _status.Dispose();
            _ok.Dispose();
            _cancel.Dispose();
        }
        base.Dispose(disposing);
    }
}
