using System.Drawing;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using MediaGenie.Rendering;

namespace MediaGenie.Forms;

/// <summary>
/// First window shown: picks which playback mode to open. Hides itself once a mode is
/// chosen; the app exits when that mode's window closes, since Application.Run treats this
/// (still-open-but-hidden) form as the main one.
/// </summary>
public sealed class LauncherForm : Form
{
    private readonly LibVLC _libVlc;

    public LauncherForm(LibVLC libVlc)
    {
        _libVlc = libVlc;

        Text = "MediaGenie";
        Icon = AppIcon.Value;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 260);
        BackColor = Theme.Panel;

        BuildLayout();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(40, 24, 40, 24)
        };

        var title = new Label
        {
            Text = "MediaGenie",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Theme.PrimaryText,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = 40
        };

        var subtitle = new Label
        {
            Text = "Choose how the videos you add should play.",
            ForeColor = Theme.MutedText,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = 30,
            Margin = new Padding(0, 0, 0, 20)
        };

        var togetherButton = new Button
        {
            Text = "Play Together (Grid)",
            Dock = DockStyle.Fill,
            Height = 56,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Tile,
            ForeColor = Theme.PrimaryText,
            Margin = new Padding(0, 0, 0, 10)
        };
        togetherButton.FlatAppearance.BorderColor = Theme.TileBorder;
        togetherButton.Click += (_, _) => Launch(new ClipPlayerForm(_libVlc));

        var sequentialButton = new Button
        {
            Text = "Play Sequentially",
            Dock = DockStyle.Fill,
            Height = 56,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Tile,
            ForeColor = Theme.PrimaryText,
            Margin = new Padding(0, 0, 0, 0)
        };
        sequentialButton.FlatAppearance.BorderColor = Theme.TileBorder;
        sequentialButton.Click += (_, _) => Launch(new SequencePlayerForm(_libVlc));

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(subtitle, 0, 1);
        layout.Controls.Add(togetherButton, 0, 2);
        layout.Controls.Add(sequentialButton, 0, 3);

        Controls.Add(layout);
    }

    private void Launch(Form target)
    {
        Hide();
        target.FormClosed += (_, _) => Close();
        target.Show();
    }
}
