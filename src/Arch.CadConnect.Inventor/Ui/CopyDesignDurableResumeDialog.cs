using System.Windows.Forms;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// P6D PRODUCTION RECOVERY: the explicit input dialog for a DURABLE Resume
/// Copy Design - reachable ONLY when this session has no in-memory
/// <c>_resumableCopyDesignAttempt</c> (e.g. after an Inventor restart), per
/// section 1's "operation-id dialog is acceptable for first implementation" -
/// never a silent scan of every operation, never a guess.
///
/// Collects the THREE explicit inputs
/// <see cref="Core.CopyDesign.Apply.CopyDesignResumeAttemptReconstructor"/>
/// needs and cannot safely infer on its own:
///   - the CopyDesignOperationId to resume (the durable, server-authoritative
///     handle - nothing here is trusted until the controller re-queries the
///     server's own status for it);
///   - the SOURCE workspace folder (where the pending/materialized
///     documents' CURRENT local files + `.arch\workspace.json` manifest
///     live - never inferred from an open document or a same-named file
///     found some other way);
///   - the DESTINATION folder the original attempt was copying into (not
///     stored server-side - see the controller's own doc comment for why).
///
/// Performs no network work, no filesystem validation, and no
/// reconstruction itself - purely collects trimmed, path-shaped input; the
/// controller does everything else (and fails closed on anything this
/// dialog could not have caught).
/// </summary>
internal sealed class CopyDesignDurableResumeDialog : Form
{
    private readonly TextBox _operationId = new() { Width = 320 };
    private readonly TextBox _sourceWorkspaceRoot = new() { Width = 260 };
    private readonly Button _browseSource = new() { Text = "Browse…", Width = 75 };
    private readonly TextBox _destinationFolder = new() { Width = 260 };
    private readonly Button _browseDestination = new() { Text = "Browse…", Width = 75 };
    private readonly Label _status = new() { AutoSize = false, Width = 380, Height = 30, ForeColor = System.Drawing.Color.Firebrick };
    private readonly Button _ok = new() { Text = "Resume Copy Design", Width = 150 };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public CopyDesignDurableResumeDialog()
    {
        Text = "Resume Copy Design (no in-session attempt found)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        AcceptButton = _ok;
        CancelButton = _cancel;

        var intro = new Label
        {
            AutoSize = false,
            Width = 380,
            Height = 48,
            Text = "This session has no remembered Copy Design attempt (e.g. Inventor was restarted). "
                + "Enter the operation's details below - they will be verified against the server's "
                + "authoritative status before anything is touched.",
        };

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control control)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
            layout.Controls.Add(control);
        }

        Row("Operation ID", _operationId);

        var sourceRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0) };
        sourceRow.Controls.Add(_sourceWorkspaceRoot);
        sourceRow.Controls.Add(_browseSource);
        Row("Source workspace folder", sourceRow);

        var destRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0) };
        destRow.Controls.Add(_destinationFolder);
        destRow.Controls.Add(_browseDestination);
        Row("Destination folder", destRow);

        layout.Controls.Add(new Label { Width = 1 });
        layout.Controls.Add(_status);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_ok);

        var root = new TableLayoutPanel { RowCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(intro);
        root.Controls.Add(layout);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _browseSource.Click += (_, _) => BrowseInto(_sourceWorkspaceRoot, "Choose the SOURCE managed workspace folder");
        _browseDestination.Click += (_, _) => BrowseInto(_destinationFolder, "Choose the DESTINATION folder the original attempt copied into");
        _ok.Click += OnOk;

        // P6D DURABLE RESUME LIVE BLOCKER fix: pre-fill from the last
        // remembered destination folder, the same convenience-only,
        // re-confirmed-every-time pattern GetLatestDialog uses for
        // ConnectSettings.LastWorkspaceRoot - never trusted, just typed
        // less often (the field most likely to be mistyped since, unlike
        // the source workspace, it has no manifest to validate against
        // until the reconstructed plan is actually built).
        _destinationFolder.Text = ConnectSettings.Load().LastCopyDesignDestinationFolder ?? string.Empty;
    }

    /// <summary>The operation id the user typed (trimmed). Valid only when
    ///  <see cref="Form.ShowDialog()"/> returned <see cref="DialogResult.OK"/>.</summary>
    public string OperationId { get; private set; } = string.Empty;

    /// <summary>The absolute SOURCE workspace folder the user chose (trimmed).</summary>
    public string SourceWorkspaceRoot { get; private set; } = string.Empty;

    /// <summary>The absolute DESTINATION folder the user chose (trimmed).</summary>
    public string DestinationFolder { get; private set; } = string.Empty;

    private static void BrowseInto(TextBox target, string description)
    {
        using var picker = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (!string.IsNullOrWhiteSpace(target.Text) && Directory.Exists(target.Text))
        {
            picker.SelectedPath = target.Text.Trim();
        }
        if (picker.ShowDialog() == DialogResult.OK)
        {
            target.Text = picker.SelectedPath;
        }
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var operationId = _operationId.Text.Trim();
        var sourceRoot = _sourceWorkspaceRoot.Text.Trim();
        var destinationFolder = _destinationFolder.Text.Trim();

        if (operationId.Length == 0)
        {
            _status.Text = "Enter the Copy Design operation id to resume.";
            return;
        }
        if (sourceRoot.Length == 0)
        {
            _status.Text = "Choose the source workspace folder.";
            return;
        }
        if (!Path.IsPathFullyQualified(sourceRoot))
        {
            _status.Text = "The source workspace folder must be a full path (e.g. C:\\Work\\ProjectX).";
            return;
        }
        if (destinationFolder.Length == 0)
        {
            _status.Text = "Choose the destination folder.";
            return;
        }
        if (!Path.IsPathFullyQualified(destinationFolder))
        {
            _status.Text = "The destination folder must be a full path (e.g. C:\\Work\\ProjectX-New).";
            return;
        }

        OperationId = operationId;
        SourceWorkspaceRoot = sourceRoot;
        DestinationFolder = destinationFolder;

        // Remember the folder (non-secret) for next time, keeping other prefs.
        (ConnectSettings.Load() with { LastCopyDesignDestinationFolder = destinationFolder }).Save();

        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _operationId.Dispose();
            _sourceWorkspaceRoot.Dispose();
            _browseSource.Dispose();
            _destinationFolder.Dispose();
            _browseDestination.Dispose();
        }
        base.Dispose(disposing);
    }
}
