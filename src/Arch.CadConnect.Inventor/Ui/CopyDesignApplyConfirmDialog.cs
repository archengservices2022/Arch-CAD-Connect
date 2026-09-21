using System.Windows.Forms;

using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// P6C: the MANDATORY explicit confirmation shown between an EXECUTABLE P6A
/// preview and the actual apply - summarizing exactly what is about to
/// happen so the user makes an informed, explicit choice. Never shown for a
/// non-executable plan (the caller is responsible for that gate - this
/// dialog itself has no "apply anyway" escape hatch). Purely a summary/
/// confirmation surface - it builds nothing, calls nothing, and performs no
/// I/O of its own.
/// </summary>
internal sealed class CopyDesignApplyConfirmDialog : Form
{
    private readonly Label _summary = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(420, 0) };
    private readonly Button _apply = new() { Text = "Apply Copy Design", Width = 140 };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public CopyDesignApplyConfirmDialog(CopyDesignPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var copyCount = plan.Nodes.Count(n => n.ProposedAction == CopyDesignAction.Copy && n.DocumentType is CadDocumentType.Ipt or CadDocumentType.Iam);
        var reuseCount = plan.Nodes.Count(n => n.ProposedAction == CopyDesignAction.Reuse && n.DocumentType is CadDocumentType.Ipt or CadDocumentType.Iam);
        var destinationRoot = plan.Nodes
            .Where(n => n.ProposedAction == CopyDesignAction.Copy && n.ProposedDestinationAbsolutePath is not null)
            .Select(n => Path.GetDirectoryName(n.ProposedDestinationAbsolutePath))
            .FirstOrDefault() ?? "(unknown)";

        Text = "Apply Copy Design - confirm";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        AcceptButton = _apply;
        CancelButton = _cancel;

        var modeLine = plan.ModelFilesOnlyAcknowledged
            // Round 8: repeat the acknowledged mode here too - the mandatory
            // confirmation must restate it, never assume the preview alone
            // was enough.
            ? "MODE: MODEL FILES ONLY - DRAWINGS: NOT INCLUDED. Drawing associations could not be proven for one " +
              "or more models; no IDW/DWG file will be copied or reference-rewired by this operation.\n\n"
            : string.Empty;

        _summary.Text =
            $"This will physically create {copyCount} new IAM/IPT file(s) and leave {reuseCount} reused file(s) untouched.\n\n" +
            $"Destination: {destinationRoot}\n\n" +
            modeLine +
            "This milestone (P6C) does NOT copy, rewire, or otherwise touch any IDW/DWG drawing - " +
            "drawings are out of scope and are never modified.\n\n" +
            "Source files are never modified. Nothing is staged, committed, or released automatically.";

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_apply);

        var root = new TableLayoutPanel { RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(_summary);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _apply.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _summary.Dispose();
            _apply.Dispose();
            _cancel.Dispose();
        }
        base.Dispose(disposing);
    }
}
