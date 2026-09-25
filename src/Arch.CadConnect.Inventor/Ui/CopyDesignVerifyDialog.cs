using System.Windows.Forms;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// P6E-D: the explicit input dialog for "Verify Copy Design" - an
/// INDEPENDENT, READ-ONLY re-verification of an existing (by operationId)
/// Copy Design operation. No copy, no rewire, no save, no materialize, no
/// repair, no reservation.
///
/// Collects exactly the THREE explicit inputs the controller needs and
/// cannot safely infer on its own:
///   - the CopyDesignOperationId to verify (the durable, server-authoritative
///     handle - nothing here is trusted until the controller re-queries the
///     server's own verification-support data for it);
///   - the SOURCE workspace folder (where the CURRENT local source files +
///     `.arch\workspace.json` manifest live - never inferred from an open
///     document or a same-named file found some other way);
///   - the DESTINATION folder the original attempt copied into (not stored
///     server-side).
///
/// NEVER guesses/finds an operation id from the filesystem - automatic
/// operation discovery is explicitly out of scope for this round.
///
/// Performs no network work and no COM work itself - only local, synchronous
/// path-shape AND existence validation (STRICTER than
/// <see cref="CopyDesignDurableResumeDialog"/>'s own path-shape-only check:
/// Verify's task explicitly requires both folders to already exist, so the
/// dialog fails fast, before any HTTP/COM call, on an obviously wrong path).
/// </summary>
internal sealed class CopyDesignVerifyDialog : Form
{
    private readonly TextBox _operationId = new() { Width = 320 };
    private readonly TextBox _sourceWorkspaceRoot = new() { Width = 260 };
    private readonly Button _browseSource = new() { Text = "Browse…", Width = 75 };
    private readonly TextBox _destinationFolder = new() { Width = 260 };
    private readonly Button _browseDestination = new() { Text = "Browse…", Width = 75 };
    private readonly Label _status = new() { AutoSize = false, Width = 380, Height = 30, ForeColor = System.Drawing.Color.Firebrick };
    private readonly Button _ok = new() { Text = "Verify Copy Design", Width = 150 };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public CopyDesignVerifyDialog()
    {
        Text = "Verify Copy Design";
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
            Text = "Independently re-verify an existing Copy Design operation. Read-only - never "
                + "copies, rewires, saves, materializes, repairs, or resumes/applies anything.",
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

        // Convenience-only pre-fill, re-confirmed (existence-checked) every
        // time on OK - reuses the SAME existing settings this add-in already
        // persists for the same concepts (GetLatestDialog/CopyDesignDurableResumeDialog),
        // rather than introducing a new remembered-path field.
        var settings = ConnectSettings.Load();
        _sourceWorkspaceRoot.Text = settings.LastWorkspaceRoot ?? string.Empty;
        _destinationFolder.Text = settings.LastCopyDesignDestinationFolder ?? string.Empty;
    }

    /// <summary>The operation id the user typed (trimmed). Valid only when
    ///  <see cref="Form.ShowDialog()"/> returned <see cref="DialogResult.OK"/>.</summary>
    public string OperationId { get; private set; } = string.Empty;

    /// <summary>The absolute, existing SOURCE workspace folder the user chose (trimmed).</summary>
    public string SourceWorkspaceRoot { get; private set; } = string.Empty;

    /// <summary>The absolute, existing DESTINATION folder the user chose (trimmed).</summary>
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
        // Fail-closed validation lives in the pure, unit-tested
        // Core.CopyDesign.Apply.CopyDesignVerifyDialogInput helper - never
        // duplicated/reimplemented here.
        var validation = Core.CopyDesign.Apply.CopyDesignVerifyDialogInput.Validate(
            _operationId.Text, _sourceWorkspaceRoot.Text, _destinationFolder.Text, Directory.Exists);
        if (!validation.IsValid)
        {
            _status.Text = validation.ErrorMessage;
            return;
        }

        OperationId = validation.OperationId;
        SourceWorkspaceRoot = validation.SourceWorkspaceRoot;
        DestinationFolder = validation.DestinationFolder;

        // Remember both folders (non-secret) for next time, reusing the
        // existing settings fields and keeping every other preference.
        (ConnectSettings.Load() with { LastWorkspaceRoot = SourceWorkspaceRoot, LastCopyDesignDestinationFolder = DestinationFolder }).Save();

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
