using System.Drawing;
using System.Windows.Forms;

using Arch.CadConnect.Core.References;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// The P5C preview step: shows the deterministic repair preview for one (or,
/// when several stale references exist, a chosen) reference and lets the
/// engineer proceed to the final confirmation - or cancel with zero CAD
/// changes. Deliberately minimal: a picker (only when needed), a read-only
/// monospace preview, and Continue / Cancel.
///
/// "Continue" here is NOT the mutation trigger - the caller still asks for an
/// explicit final confirmation immediately before touching Inventor.
/// </summary>
internal sealed class RepairReferenceDialog : Form
{
    private readonly IReadOnlyList<ReferenceRepairPlan> _plans;
    private readonly ListBox? _picker;
    private readonly TextBox _preview;
    private readonly Button _continue = new() { Text = "Continue…", DialogResult = DialogResult.OK, Width = 110, Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public RepairReferenceDialog(string title, IReadOnlyList<ReferenceRepairPlan> plans)
    {
        _plans = plans;

        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowIcon = false;
        ClientSize = new Size(720, 520);
        MinimumSize = new Size(480, 320);
        Padding = new Padding(12);
        AcceptButton = _continue;
        CancelButton = _cancel;

        _preview = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_continue);

        // Same add-order convention as ScanResultDialog: the Fill control first,
        // then the edge-docked controls.
        Controls.Add(_preview);

        if (_plans.Count > 1)
        {
            _picker = new ListBox { Dock = DockStyle.Top, Height = 96, IntegralHeight = false };
            foreach (var plan in _plans)
            {
                _picker.Items.Add(LabelFor(plan));
            }
            _picker.SelectedIndexChanged += (_, _) => ShowSelected();
            Controls.Add(_picker);
            Controls.Add(new Label
            {
                Text = "Stale managed references (select one to repair):",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(0, 0, 0, 4),
            });
        }

        Controls.Add(buttons);

        Shown += (_, _) =>
        {
            if (_picker is not null)
            {
                _picker.SelectedIndex = 0;
            }
            else
            {
                ShowIndex(0);
            }
        };
    }

    /// <summary>The plan the engineer chose to proceed with (only meaningful
    ///  when the dialog returned <see cref="DialogResult.OK"/>).</summary>
    public ReferenceRepairPlan? SelectedPlan { get; private set; }

    private void ShowSelected() => ShowIndex(_picker?.SelectedIndex ?? 0);

    private void ShowIndex(int index)
    {
        if (index < 0 || index >= _plans.Count)
        {
            SelectedPlan = null;
            _preview.Text = "(no reference selected)";
            _continue.Enabled = false;
            return;
        }

        var plan = _plans[index];
        SelectedPlan = plan;
        _preview.Text = ReferenceRepairTextReport.Render(plan).ReplaceLineEndings();
        _preview.Select(0, 0);
        _continue.Enabled = plan.CanProceed;
    }

    private static string LabelFor(ReferenceRepairPlan plan)
    {
        var name = plan.ObservedReferenceName;
        try
        {
            var file = Path.GetFileName(plan.CurrentReferencePath ?? plan.ObservedReferenceName);
            if (!string.IsNullOrWhiteSpace(file))
            {
                name = file;
            }
        }
        catch (ArgumentException)
        {
            // keep the observed name
        }
        return $"[{plan.EligibilityLabel}] {name}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _picker?.Dispose();
            _preview.Dispose();
            _continue.Dispose();
            _cancel.Dispose();
        }
        base.Dispose(disposing);
    }
}
