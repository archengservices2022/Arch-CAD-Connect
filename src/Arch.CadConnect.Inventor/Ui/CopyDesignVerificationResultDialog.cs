using System.Drawing;
using System.Windows.Forms;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// P6E-D: a dedicated, readable, WORD-WRAPPING view of a Copy Design
/// verification report. Deliberately NOT <see cref="ScanResultDialog"/>
/// (which uses <c>WordWrap = false</c> + horizontal scrolling - exactly the
/// "P6D horizontal-truncation problem" a verification report's long per-check
/// reasons must avoid): every line here wraps, so no reason is ever cut off
/// or requires horizontal scrolling to read.
///
/// Read-only - a scrollable, word-wrapped monospace box, a Close button, and
/// an optional Copy to Clipboard button.
/// </summary>
internal sealed class CopyDesignVerificationResultDialog : Form
{
    private readonly TextBox _body;
    private readonly Button _copy = new() { Text = "Copy to Clipboard", Width = 140 };
    private readonly Button _close = new() { Text = "Close", DialogResult = DialogResult.OK, Width = 90 };

    public CopyDesignVerificationResultDialog(string title, string report)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowIcon = false;
        ClientSize = new Size(760, 540);
        MinimumSize = new Size(480, 320);
        Padding = new Padding(12);
        AcceptButton = _close;
        CancelButton = _close;

        _body = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9f),
            Text = report.ReplaceLineEndings(),
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_copy);

        Controls.Add(_body);
        Controls.Add(buttons);

        _copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_body.Text); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException) { /* best effort */ }
        };

        Shown += (_, _) => _body.Select(0, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _body.Dispose();
            _copy.Dispose();
            _close.Dispose();
        }
        base.Dispose(disposing);
    }
}
