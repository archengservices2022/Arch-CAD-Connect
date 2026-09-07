using System.Drawing;
using System.Windows.Forms;

namespace Arch.CadConnect.Inventor.Ui;

/// <summary>
/// A deterministic, read-only text view of a P5A reference scan. Not polished
/// UI - a scrollable monospace box and a Close button - just enough that a
/// human can read a multi-line reference report that would not fit a
/// <see cref="MessageBox"/>.
/// </summary>
internal sealed class ScanResultDialog : Form
{
    private readonly TextBox _body;
    private readonly Button _close = new() { Text = "Close", DialogResult = DialogResult.OK, Width = 90 };

    public ScanResultDialog(string title, string report)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowIcon = false;
        ClientSize = new Size(680, 460);
        MinimumSize = new Size(420, 260);
        Padding = new Padding(12);
        AcceptButton = _close;
        CancelButton = _close;

        _body = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
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

        Controls.Add(_body);
        Controls.Add(buttons);

        Shown += (_, _) => _body.Select(0, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _body.Dispose();
            _close.Dispose();
        }
        base.Dispose(disposing);
    }
}
