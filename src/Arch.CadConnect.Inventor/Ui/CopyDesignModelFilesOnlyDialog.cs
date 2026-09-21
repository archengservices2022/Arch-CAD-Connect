using System.Windows.Forms;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// P6C Round 8: the ONLY supported way to waive the "drawing association
/// authority is unavailable" blocker for an otherwise-safe IAM/IPT plan.
/// Shown ONLY when the current plan is not executable SOLELY because drawing
/// association completeness could not be proven (the caller decides that -
/// this dialog performs no planning of its own). Requires an explicit,
/// affirmative click - there is no pre-selected/default "Yes": the default
/// safe outcome of closing, cancelling, or pressing Enter is always "do NOT
/// continue".
/// </summary>
internal sealed class CopyDesignModelFilesOnlyDialog : Form
{
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(440, 0) };
    private readonly Button _continue = new() { Text = "Continue - Model Files Only", Width = 190, DialogResult = DialogResult.None };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };

    public CopyDesignModelFilesOnlyDialog()
    {
        Text = "Copy Design - drawings cannot be confirmed";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        // Deliberately NO AcceptButton - pressing Enter must never be
        // mistaken for an affirmative choice. Only an explicit click on
        // "Continue - Model Files Only" can proceed.
        CancelButton = _cancel;

        _message.Text =
            "Drawing associations cannot be proven for one or more models in this plan.\n\n" +
            "Continue with model files only (IAM/IPT)?\n\n" +
            "Associated IDW/DWG files will NOT be copied in this operation.";

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_continue);

        var root = new TableLayoutPanel { RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(_message);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _continue.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _message.Dispose();
            _continue.Dispose();
            _cancel.Dispose();
        }
        base.Dispose(disposing);
    }
}
