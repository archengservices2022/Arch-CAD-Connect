using System.Windows.Forms;

using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// P6D ROUND 3, item F: the MANDATORY explicit confirmation shown before a
/// Recover Copy Design attempt - DELIBERATELY separate from both
/// <see cref="CopyDesignApplyConfirmDialog"/> and
/// <see cref="CopyDesignResumeConfirmDialog"/> so a recovery of an UNCERTAIN
/// attempt (no confirmed server reservation at all) is never mistaken for
/// either an ordinary fresh apply or a resume of a CONFIRMED-but-unmaterialized
/// one. Names the EXACT idempotency key being reused, and is explicit that the
/// outcome of the earlier attempt is genuinely unknown - the engineer can see
/// this replays that SAME request rather than starting a new one. Purely a
/// summary/confirmation surface - it builds nothing, calls nothing, and
/// performs no I/O of its own.
/// </summary>
internal sealed class CopyDesignRecoverConfirmDialog : Form
{
    private readonly Label _summary = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(440, 0) };
    private readonly Button _recover = new() { Text = "Recover Copy Design", Width = 160 };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public CopyDesignRecoverConfirmDialog(CopyDesignPlan plan, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        var copyCount = plan.Nodes.Count(n => n.ProposedAction == CopyDesignAction.Copy);

        Text = "Recover Copy Design - confirm";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        AcceptButton = _recover;
        CancelButton = _cancel;

        _summary.Text =
            $"The outcome of the LAST Copy Design attempt (idempotency key: {idempotencyKey}) is UNKNOWN - its " +
            "reservation request itself never returned a trustworthy answer (a network/timeout/server problem), " +
            "so it is not known whether Arch PLM actually committed it.\n\n" +
            "Recovering REPLAYS the EXACT SAME request with the SAME idempotency key - it never creates a new " +
            "request. If the server already committed the earlier attempt, the SAME operation is returned and " +
            $"nothing is duplicated; if it did not, it is safely created now. Up to {copyCount} " +
            $"entr{(copyCount == 1 ? "y" : "ies")} will be processed.\n\n" +
            "Source files are never modified. No new CadDocument identity is ever created beyond what this ONE " +
            "logical attempt would have created.";

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_recover);

        var root = new TableLayoutPanel { RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(_summary);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _recover.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _summary.Dispose();
            _recover.Dispose();
            _cancel.Dispose();
        }
        base.Dispose(disposing);
    }
}
