using System.Windows.Forms;

using Arch.CadConnect.Core.CopyDesign;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// P6C Round 6: the ONLY supported way to resolve a
/// <see cref="CopyDesignAction.NeedsDecision"/> node that exists solely
/// because no library/shared classification signal was available (see
/// <see cref="CopyDesignExplicitDecisionEligibility"/>). Lists EXACTLY the
/// eligible nodes the caller passes in - never all nodes, never a guess - and
/// requires the engineer to explicitly pick COPY, REUSE, or EXCLUDE for EVERY
/// one before OK is accepted. The "Suggested: COPY" text is shown as a plain
/// hint only; nothing is pre-selected, so a suggestion can never be silently
/// carried through by an engineer who just clicks through the dialog.
///
/// This dialog performs no planning, no scanning, and no filesystem I/O of
/// its own - it only collects a validated <see cref="CopyDesignExplicitDecisionSet"/>
/// keyed by each node's OWN stable <c>CadDocumentId</c>, for the caller to
/// feed back into <see cref="CopyDesignPlanner.Plan"/> for recomputation.
/// </summary>
internal sealed class CopyDesignDecisionsDialog : Form
{
    private const string ChoosePrompt = "- choose -";

    private readonly List<(CopyDesignNode Node, ComboBox Combo)> _rows = new();
    private readonly Label _status = new() { AutoSize = false, Width = 480, Height = 30, ForeColor = System.Drawing.Color.Firebrick };
    private readonly Button _ok = new() { Text = "Recompute Plan", DialogResult = DialogResult.None, Width = 130 };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };

    public CopyDesignDecisionsDialog(IReadOnlyList<CopyDesignNode> eligibleNodes)
    {
        ArgumentNullException.ThrowIfNull(eligibleNodes);
        if (eligibleNodes.Count == 0)
        {
            throw new ArgumentException("At least one eligible NeedsDecision node is required.", nameof(eligibleNodes));
        }
        foreach (var node in eligibleNodes)
        {
            if (!CopyDesignExplicitDecisionEligibility.IsEligibleForExplicitDecision(node))
            {
                throw new ArgumentException(
                    $"\"{node.SourceFileName}\" is not eligible for an explicit decision - only a NeedsDecision node whose sole " +
                    "blocker is an unavailable library/shared classification signal may be listed here.",
                    nameof(eligibleNodes));
            }
        }

        Text = "Copy Design - resolve decisions";
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
            AutoSize = true,
            MaximumSize = new System.Drawing.Size(520, 0),
            Margin = new Padding(0, 0, 0, 10),
            Text = "No library/shared classification signal is available for the component(s) below - the planner refuses to " +
                "guess. Choose COPY, REUSE, or EXCLUDE for EACH one; nothing is pre-selected.",
        };

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        foreach (var node in eligibleNodes)
        {
            var nameLabel = new Label
            {
                Text = node.SourceFileName,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 0),
            };
            var hintLabel = new Label
            {
                Text = "(Suggested: COPY)",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 0),
                ForeColor = System.Drawing.SystemColors.GrayText,
            };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            combo.Items.AddRange(new object[] { ChoosePrompt, "Copy", "Reuse", "Exclude" });
            combo.SelectedIndex = 0; // NEVER pre-select a real action - the suggestion is a hint, not a default.

            grid.Controls.Add(nameLabel);
            grid.Controls.Add(hintLabel);
            grid.Controls.Add(combo);
            _rows.Add((node, combo));
        }

        grid.Controls.Add(new Label { Width = 1 });
        grid.Controls.Add(new Label { Width = 1 });
        grid.Controls.Add(_status);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_ok);

        var root = new TableLayoutPanel { RowCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(intro);
        root.Controls.Add(grid);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _ok.Click += OnOk;
    }

    public CopyDesignExplicitDecisionSet Decisions { get; private set; } = CopyDesignExplicitDecisionSet.Empty;

    private void OnOk(object? sender, EventArgs e)
    {
        var pairs = new List<(string CadDocumentId, CopyDesignAction Action)>();
        foreach (var (node, combo) in _rows)
        {
            var choice = (string)combo.SelectedItem!;
            if (choice == ChoosePrompt)
            {
                _status.Text = $"Choose COPY, REUSE, or EXCLUDE for \"{node.SourceFileName}\" before recomputing.";
                return;
            }
            var action = choice switch
            {
                "Copy" => CopyDesignAction.Copy,
                "Reuse" => CopyDesignAction.Reuse,
                "Exclude" => CopyDesignAction.Exclude,
                _ => throw new InvalidOperationException($"Unrecognized decision choice \"{choice}\"."),
            };
            pairs.Add((node.CadDocumentId!, action));
        }

        Decisions = CopyDesignExplicitDecisionSet.Build(pairs);
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var (_, combo) in _rows)
            {
                combo.Dispose();
            }
            _status.Dispose();
            _ok.Dispose();
            _cancel.Dispose();
        }
        base.Dispose(disposing);
    }
}
