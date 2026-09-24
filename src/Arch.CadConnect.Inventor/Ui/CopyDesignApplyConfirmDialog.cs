using System.Windows.Forms;

using Arch.CadConnect.Core;
using Arch.CadConnect.Core.CopyDesign;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// P6C/P6D: the MANDATORY explicit confirmation shown between an EXECUTABLE
/// P6A preview and the actual apply - summarizing exactly what is about to
/// happen so the user makes an informed, explicit choice. Never shown for a
/// non-executable plan (the caller is responsible for that gate - this
/// dialog itself has no "apply anyway" escape hatch). Purely a summary/
/// confirmation surface - it builds nothing, calls nothing, and performs no
/// I/O of its own.
///
/// P6D fix: this dialog previously told the user "drawings are out of scope
/// and are never modified" UNCONDITIONALLY - that became FALSE the moment
/// P6D started executing drawings, and showing false information in a
/// mandatory confirmation immediately before a mutating operation is a real
/// correctness bug, not a cosmetic one. The summary now counts and reports
/// drawing (IDW/DWG) Copy/Reuse entries exactly like model entries, and only
/// claims "no drawing is copied/rewired" when that is actually true (the
/// explicit model-files-only acknowledgement).
/// </summary>
internal sealed class CopyDesignApplyConfirmDialog : Form
{
    private readonly Label _summary = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(420, 0) };
    private readonly Button _apply = new() { Text = "Apply Copy Design", Width = 140 };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public CopyDesignApplyConfirmDialog(CopyDesignPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var modelCopyCount = plan.Nodes.Count(n => n.ProposedAction == CopyDesignAction.Copy && n.DocumentType is CadDocumentType.Ipt or CadDocumentType.Iam);
        var modelReuseCount = plan.Nodes.Count(n => n.ProposedAction == CopyDesignAction.Reuse && n.DocumentType is CadDocumentType.Ipt or CadDocumentType.Iam);
        var drawingCopyCount = plan.Nodes.Count(n => n.ProposedAction == CopyDesignAction.Copy && n.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg);
        var drawingReuseCount = plan.Nodes.Count(n => n.ProposedAction == CopyDesignAction.Reuse && n.DocumentType is CadDocumentType.Idw or CadDocumentType.Dwg);
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

        // Drawings execute normally UNLESS the plan itself carries the
        // explicit model-files-only acknowledgement - see
        // CopyDesignApplyOrchestrator's own class doc comment. That
        // acknowledgement is the ONLY case where "no drawing is touched" is
        // actually true, so it is the only case that says so.
        string modeLine;
        string drawingLine;
        if (plan.ModelFilesOnlyAcknowledged)
        {
            // Round 8: repeat the acknowledged mode here too - the mandatory
            // confirmation must restate it, never assume the preview alone
            // was enough.
            modeLine = "MODE: MODEL FILES ONLY - DRAWINGS: NOT INCLUDED. Drawing associations could not be proven for one " +
                "or more models; no IDW/DWG file will be copied or reference-rewired by this operation.\n\n";
            drawingLine = string.Empty;
        }
        else
        {
            modeLine = string.Empty;
            drawingLine = drawingCopyCount > 0 || drawingReuseCount > 0
                ? $"This will ALSO physically create {drawingCopyCount} new IDW/DWG drawing file(s) and rewire their " +
                  $"model reference(s) to the copied/reused targets above, leaving {drawingReuseCount} reused drawing " +
                  "file(s) untouched.\n\n"
                : string.Empty;
        }

        _summary.Text =
            $"This will physically create {modelCopyCount} new IAM/IPT file(s) and leave {modelReuseCount} reused file(s) untouched.\n\n" +
            $"Destination: {destinationRoot}\n\n" +
            modeLine +
            drawingLine +
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
