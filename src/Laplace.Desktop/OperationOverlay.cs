namespace Laplace.Desktop;

internal sealed class OperationOverlay : UserControl
{
    private static readonly Color AccentColor = Color.FromArgb(45, 92, 170);
    private readonly Label _message = new();
    private readonly Label _percent = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _cancel = new();

    public OperationOverlay()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(242, 246, 252);
        Visible = false;
        AccessibleName = "Operation progress";

        var canvas = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            BackColor = BackColor
        };
        canvas.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        canvas.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 500));
        canvas.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        canvas.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        canvas.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        canvas.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(30, 26, 30, 24),
            BorderStyle = BorderStyle.FixedSingle
        };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "Laplace is working",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 15F),
            ForeColor = Color.FromArgb(28, 39, 56),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var subtitle = new Label
        {
            Text = "You can keep this window open while the operation completes.",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(99, 112, 130),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _message.Dock = DockStyle.Fill;
        _message.Font = new Font("Segoe UI Semibold", 9.5F);
        _message.ForeColor = Color.FromArgb(41, 53, 70);
        _message.TextAlign = ContentAlignment.BottomLeft;
        _message.AutoEllipsis = true;

        _percent.Dock = DockStyle.Fill;
        _percent.Font = new Font("Segoe UI Semibold", 9.5F);
        _percent.ForeColor = AccentColor;
        _percent.TextAlign = ContentAlignment.BottomRight;

        _progress.Dock = DockStyle.Fill;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 24;
        _progress.AccessibleName = "Operation progress";

        _cancel.Text = "Cancel";
        _cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _cancel.Size = new Size(92, 32);
        _cancel.FlatStyle = FlatStyle.System;
        _cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 6, 0, 0)
        };
        footer.Controls.Add(_cancel);

        content.Controls.Add(title, 0, 0);
        content.SetColumnSpan(title, 2);
        content.Controls.Add(subtitle, 0, 1);
        content.SetColumnSpan(subtitle, 2);
        content.Controls.Add(_message, 0, 2);
        content.Controls.Add(_percent, 1, 2);
        content.Controls.Add(_progress, 0, 3);
        content.SetColumnSpan(_progress, 2);
        content.Controls.Add(footer, 0, 5);
        content.SetColumnSpan(footer, 2);
        card.Controls.Add(content);
        canvas.Controls.Add(card, 1, 1);
        Controls.Add(canvas);
    }

    public event EventHandler? CancelRequested;

    public void ShowOperation(bool canCancel)
    {
        _message.Text = "Preparing operation...";
        _percent.Text = string.Empty;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 24;
        _cancel.Visible = canCancel;
        _cancel.Enabled = canCancel;
        UseWaitCursor = !canCancel;
    }

    public void SetProgress(string message, int percent)
    {
        _message.Text = string.IsNullOrWhiteSpace(message) ? "Working..." : message;
        var value = Math.Clamp(percent, 0, 100);
        if (value <= 0)
        {
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.MarqueeAnimationSpeed = 24;
            _percent.Text = string.Empty;
            return;
        }

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = value;
        _percent.Text = $"{value}%";
    }

    public void SetCancelling()
    {
        _message.Text = "Finishing the current step before cancelling...";
        _cancel.Enabled = false;
        _cancel.Text = "Cancelling...";
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            _cancel.Text = "Cancel";
            UseWaitCursor = false;
        }
    }
}
