namespace DumpTriageTray;

/// <summary>
/// The "ask first" prompt. Note this is a lightweight always-on-top popup
/// anchored above the tray, not a true actionable Windows Action Center
/// toast: actionable toast buttons for an unpackaged Win32 app require
/// registering an AppUserModelID and a COM notification activator via the
/// installer, which is real added complexity for marginal UX gain over a
/// borderless popup + a passive balloon tip. This can be upgraded to a real
/// toast later (see README) without changing the IPC protocol — the tray is
/// the only thing that would change.
/// </summary>
public sealed class ApprovalPopupForm : Form
{
    private readonly System.Windows.Forms.Timer _countdownTimer = new() { Interval = 1000 };
    private int _secondsRemaining;
    private readonly Label _countdownLabel;

    public bool? Result { get; private set; }

    public ApprovalPopupForm(string fileName, long sizeBytes, string dumpType, int timeoutSeconds)
    {
        _secondsRemaining = timeoutSeconds;

        Text = "DumpTriage";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        Width = 360;
        Height = 160;
        Padding = new Padding(12);

        var messageLabel = new Label
        {
            Text = $"A new {dumpType} dump was captured:\n{fileName} ({FormatSize(sizeBytes)})\n\nSubmit it to DumpTriage for analysis?",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 80,
        };

        _countdownLabel = new Label
        {
            Text = $"Skipping automatically in {_secondsRemaining}s",
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = SystemColors.GrayText,
        };

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 36,
        };

        var submitButton = new Button { Text = "Submit", AutoSize = true };
        submitButton.Click += (_, _) => Finish(true);

        var skipButton = new Button { Text = "Skip", AutoSize = true };
        skipButton.Click += (_, _) => Finish(false);

        buttonPanel.Controls.Add(submitButton);
        buttonPanel.Controls.Add(skipButton);

        Controls.Add(messageLabel);
        Controls.Add(_countdownLabel);
        Controls.Add(buttonPanel);

        AcceptButton = submitButton;
        CancelButton = skipButton;

        _countdownTimer.Tick += (_, _) =>
        {
            _secondsRemaining--;
            _countdownLabel.Text = $"Skipping automatically in {_secondsRemaining}s";
            if (_secondsRemaining <= 0) Finish(false);
        };

        Load += (_, _) =>
        {
            var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            Location = new Point(workingArea.Right - Width - 16, workingArea.Bottom - Height - 16);
            _countdownTimer.Start();
        };
    }

    private void Finish(bool approved)
    {
        Result = approved;
        _countdownTimer.Stop();
        Close();
    }

    private static string FormatSize(long bytes)
    {
        double size = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:0.#} {units[unitIndex]}";
    }
}
