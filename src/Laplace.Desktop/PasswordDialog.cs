using Laplace.Core.Models;

namespace Laplace.Desktop;

internal sealed class PasswordDialog : Form
{
    private static readonly Color AccentColor = Color.FromArgb(38, 78, 148);
    private readonly TextBox _password = new();
    private readonly Label _feedback = new();
    private readonly CheckBox _showPassword = new();

    public PasswordDialog(
        string title,
        string archivePath,
        string description,
        bool isError = false)
    {
        Text = title;
        ClientSize = new Size(500, 330);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.White
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));

        var header = BuildHeader(title, description);
        var body = BuildBody(archivePath, isError);
        var footer = BuildFooter();

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(body, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        Shown += (_, _) =>
        {
            _password.Focus();
            UpdateCapsLockWarning();
        };
    }

    public PasswordContext? Password => PasswordContext.FromNullable(_password.Text);

    private Control BuildHeader(string title, string description)
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AccentColor,
            Padding = new Padding(22, 18, 22, 14),
            ColumnCount = 2,
            RowCount = 2
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var icon = new PictureBox
        {
            Image = SystemIcons.Shield.ToBitmap(),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Fill
        };
        var heading = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 15F),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var help = new Label
        {
            Text = description,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(222, 232, 249),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true
        };

        header.Controls.Add(icon, 0, 0);
        header.SetRowSpan(icon, 2);
        header.Controls.Add(heading, 1, 0);
        header.Controls.Add(help, 1, 1);
        return header;
    }

    private Control BuildBody(string archivePath, bool isError)
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 16, 24, 8),
            ColumnCount = 1,
            RowCount = 6
        };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var archiveLabel = new Label
        {
            Text = "ARCHIVE",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 8F),
            ForeColor = Color.FromArgb(104, 116, 133),
            TextAlign = ContentAlignment.BottomLeft
        };
        var archiveName = new TextBox
        {
            Text = Path.GetFileName(archivePath),
            ReadOnly = true,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(246, 248, 251),
            ForeColor = Color.FromArgb(48, 59, 74),
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "Archive name"
        };
        var passwordLabel = new Label
        {
            Text = "PASSWORD",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 8F),
            ForeColor = Color.FromArgb(104, 116, 133),
            TextAlign = ContentAlignment.BottomLeft
        };

        _password.Dock = DockStyle.Fill;
        _password.UseSystemPasswordChar = true;
        _password.AccessibleName = "Archive password";
        _password.KeyUp += (_, _) => UpdateCapsLockWarning();
        _password.TextChanged += (_, _) =>
        {
            if (_feedback.ForeColor == Color.Firebrick)
            {
                _feedback.Text = string.Empty;
            }
        };

        _showPassword.Text = "Show password";
        _showPassword.AutoSize = true;
        _showPassword.ForeColor = Color.FromArgb(72, 84, 101);
        _showPassword.CheckedChanged += (_, _) =>
        {
            _password.UseSystemPasswordChar = !_showPassword.Checked;
            _password.Focus();
            _password.SelectionStart = _password.TextLength;
        };

        _feedback.Dock = DockStyle.Fill;
        _feedback.ForeColor = isError ? Color.Firebrick : Color.FromArgb(154, 96, 0);
        _feedback.Text = isError ? "The previous password was not accepted." : string.Empty;
        _feedback.TextAlign = ContentAlignment.MiddleLeft;

        var helperRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        helperRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        helperRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        helperRow.Controls.Add(_feedback, 0, 0);
        helperRow.Controls.Add(_showPassword, 1, 0);

        body.Controls.Add(archiveLabel, 0, 0);
        body.Controls.Add(archiveName, 0, 1);
        body.Controls.Add(passwordLabel, 0, 2);
        body.Controls.Add(_password, 0, 3);
        body.Controls.Add(helperRow, 0, 4);
        return body;
    }

    private Control BuildFooter()
    {
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(18, 13, 18, 10),
            BackColor = Color.FromArgb(246, 248, 251)
        };
        var unlock = new Button
        {
            Text = "Unlock",
            Width = 100,
            Height = 32,
            BackColor = AccentColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        unlock.FlatAppearance.BorderSize = 0;
        unlock.Click += (_, _) => SubmitPassword();

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = 92,
            Height = 32
        };
        footer.Controls.Add(unlock);
        footer.Controls.Add(cancel);
        AcceptButton = unlock;
        CancelButton = cancel;
        return footer;
    }

    private void SubmitPassword()
    {
        if (string.IsNullOrEmpty(_password.Text))
        {
            _feedback.ForeColor = Color.Firebrick;
            _feedback.Text = "Enter the archive password.";
            _password.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
    }

    private void UpdateCapsLockWarning()
    {
        if (IsKeyLocked(Keys.CapsLock))
        {
            _feedback.ForeColor = Color.FromArgb(154, 96, 0);
            _feedback.Text = "Caps Lock is on.";
        }
        else if (_feedback.Text == "Caps Lock is on.")
        {
            _feedback.Text = string.Empty;
        }
    }
}
