using System.Windows.Forms;

using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// P6D ROUND 2, HIGH fix (RESUME): the MANDATORY explicit confirmation shown
/// before a Resume Copy Design attempt - DELIBERATELY separate from
/// <see cref="CopyDesignApplyConfirmDialog"/> so a resume is never mistaken
/// for (or silently treated as) an ordinary fresh apply. Names the EXACT
/// idempotency key being reused, so the engineer can see this is a
/// continuation of a specific prior attempt, not a new one. Purely a
/// summary/confirmation surface - it builds nothing, calls nothing, and
/// performs no I/O of its own.
/// </summary>
internal sealed class CopyDesignResumeConfirmDialog : Form
{
    private readonly Label _summary = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(440, 0) };
    private readonly Button _resume = new() { Text = "Resume Copy Design", Width = 160 };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public CopyDesignResumeConfirmDialog(CopyDesignPlan plan, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        var copyCount = plan.Nodes.Count(n => n.ProposedAction == CopyDesignAction.Copy);

        Text = "Resume Copy Design - confirm";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        AcceptButton = _resume;
        CancelButton = _cancel;

        _summary.Text =
            $"This CONTINUES the SAME Copy Design operation (idempotency key: {idempotencyKey}) that was previously " +
            "reserved but did not fully materialize - it does NOT create a new Copy Design request.\n\n" +
            $"Up to {copyCount} entr{(copyCount == 1 ? "y" : "ies")} will be checked against the server: anything " +
            "already materialized in the prior attempt is left exactly as-is (never re-copied, never a second " +
            "FileVersion 1); anything still pending is completed normally.\n\n" +
            "Source files are never modified. No new CadDocument identity is ever created by this resume.";

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_resume);

        var root = new TableLayoutPanel { RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(_summary);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _resume.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _summary.Dispose();
            _resume.Dispose();
            _cancel.Dispose();
        }
        base.Dispose(disposing);
    }
}
