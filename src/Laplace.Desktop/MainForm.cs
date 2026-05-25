using Laplace.Compression;
using Laplace.Core.Enums;
using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using Laplace.Core.Services;
using CompressionMode = Laplace.Core.Enums.CompressionMode;

namespace Laplace.Desktop;

public sealed class MainForm : Form
{
    private readonly UniversalArchiveService _archives = new(new CompressorRegistry());
    private readonly TextBox _archivePathText = new();
    private readonly ListView _entriesView = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _summaryLabel = new();
    private readonly ToolStripProgressBar _progressBar = new();
    private readonly ToolStripButton _extractButton = new();
    private readonly ToolStripButton _testButton = new();
    private readonly ToolStripButton _infoButton = new();

    private string? _currentArchivePath;
    private PasswordContext? _currentPassword;
    private ArchiveSummary? _currentSummary;

    public MainForm(string[] args)
    {
        Text = "Laplace";
        ClientSize = new Size(1080, 680);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        Icon = LoadAppIcon();
        AllowDrop = true;

        var menu = BuildMenu();
        var toolbar = BuildToolbar();
        var addressPanel = BuildAddressPanel();
        var status = BuildStatusBar();

        ConfigureEntryList();

        Controls.Add(_entriesView);
        Controls.Add(addressPanel);
        Controls.Add(toolbar);
        Controls.Add(menu);
        Controls.Add(status);
        MainMenuStrip = menu;

        DragEnter += MainForm_DragEnter;
        DragDrop += MainForm_DragDrop;

        UpdateArchiveState(null, null);
        Shown += (_, _) => ApplyStartupArgs(args);
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Open archive...", null, async (_, _) => await ChooseAndOpenArchiveAsync().ConfigureAwait(true));
        file.DropDownItems.Add("&Create archive...", null, async (_, _) => await ShowCreateDialogAsync([]).ConfigureAwait(true));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var commands = new ToolStripMenuItem("&Commands");
        commands.DropDownItems.Add("&Extract to...", null, async (_, _) => await ShowExtractDialogAsync().ConfigureAwait(true));
        commands.DropDownItems.Add("&Test archive", null, async (_, _) => await TestCurrentArchiveAsync().ConfigureAwait(true));
        commands.DropDownItems.Add("&Archive information", null, (_, _) => ShowArchiveInfo());

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("&Clear password", null, (_, _) =>
        {
            _currentPassword = null;
            SetStatus("Password cleared.", 0);
        });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("&About Laplace", null, (_, _) => MessageBox.Show(
            this,
            "Laplace archive manager\nNative .lpc and common archive support.",
            "About Laplace",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information));

        menu.Items.AddRange([file, commands, tools, help]);
        menu.Dock = DockStyle.Top;
        return menu;
    }

    private ToolStrip BuildToolbar()
    {
        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 62,
            GripStyle = ToolStripGripStyle.Hidden,
            ImageScalingSize = new Size(24, 24),
            Padding = new Padding(6, 4, 6, 4)
        };

        toolbar.Items.Add(CreateToolButton("Add", SystemIcons.Application, async (_, _) => await ShowCreateDialogAsync([]).ConfigureAwait(true)));
        _extractButton.Text = "Extract";
        _extractButton.Image = SystemIcons.Shield.ToBitmap();
        _extractButton.TextImageRelation = TextImageRelation.ImageAboveText;
        _extractButton.AutoSize = false;
        _extractButton.Size = new Size(72, 52);
        _extractButton.Click += async (_, _) => await ShowExtractDialogAsync().ConfigureAwait(true);
        toolbar.Items.Add(_extractButton);

        _testButton.Text = "Test";
        _testButton.Image = SystemIcons.Information.ToBitmap();
        _testButton.TextImageRelation = TextImageRelation.ImageAboveText;
        _testButton.AutoSize = false;
        _testButton.Size = new Size(72, 52);
        _testButton.Click += async (_, _) => await TestCurrentArchiveAsync().ConfigureAwait(true);
        toolbar.Items.Add(_testButton);

        toolbar.Items.Add(CreateToolButton("Open", SystemIcons.WinLogo, async (_, _) => await ChooseAndOpenArchiveAsync().ConfigureAwait(true)));

        _infoButton.Text = "Info";
        _infoButton.Image = SystemIcons.Question.ToBitmap();
        _infoButton.TextImageRelation = TextImageRelation.ImageAboveText;
        _infoButton.AutoSize = false;
        _infoButton.Size = new Size(72, 52);
        _infoButton.Click += (_, _) => ShowArchiveInfo();
        toolbar.Items.Add(_infoButton);

        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(CreateToolButton("Delete", SystemIcons.Error, (_, _) => ShowValidation("Deleting entries inside an archive is not supported yet.")));
        toolbar.Items.Add(CreateToolButton("Find", SystemIcons.Asterisk, (_, _) => FocusSearch()));
        return toolbar;
    }

    private Panel BuildAddressPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(8, 6, 8, 5)
        };
        var label = new Label
        {
            Text = "Archive:",
            Dock = DockStyle.Left,
            Width = 58,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var browse = new Button
        {
            Text = "...",
            Dock = DockStyle.Right,
            Width = 38
        };
        browse.Click += async (_, _) => await ChooseAndOpenArchiveAsync().ConfigureAwait(true);
        _archivePathText.Dock = DockStyle.Fill;
        _archivePathText.ReadOnly = true;

        panel.Controls.Add(_archivePathText);
        panel.Controls.Add(browse);
        panel.Controls.Add(label);
        return panel;
    }

    private StatusStrip BuildStatusBar()
    {
        var status = new StatusStrip();
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Text = "Ready";
        _summaryLabel.Text = string.Empty;
        _progressBar.Width = 180;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        status.Items.AddRange([_statusLabel, _summaryLabel, _progressBar]);
        return status;
    }

    private void ConfigureEntryList()
    {
        _entriesView.Dock = DockStyle.Fill;
        _entriesView.View = View.Details;
        _entriesView.FullRowSelect = true;
        _entriesView.GridLines = true;
        _entriesView.HideSelection = false;
        _entriesView.MultiSelect = true;
        _entriesView.SmallImageList = BuildImageList();
        _entriesView.Columns.Add("Name", 280);
        _entriesView.Columns.Add("Size", 110, HorizontalAlignment.Right);
        _entriesView.Columns.Add("Packed", 110, HorizontalAlignment.Right);
        _entriesView.Columns.Add("Type", 90);
        _entriesView.Columns.Add("Modified", 140);
        _entriesView.Columns.Add("Method", 130);
        _entriesView.Columns.Add("Encrypted", 80);
        _entriesView.Columns.Add("Path", 360);
        _entriesView.DoubleClick += async (_, _) =>
        {
            if (_currentArchivePath is not null)
            {
                await ShowExtractDialogAsync().ConfigureAwait(true);
            }
        };
    }

    private static ImageList BuildImageList()
    {
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
        images.Images.Add("file", SystemIcons.Application);
        images.Images.Add("folder", SystemIcons.WinLogo);
        return images;
    }

    private async Task ChooseAndOpenArchiveAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open archive",
            Filter = "Archive files|*.lpc;*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.zst;*.lzip|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await LoadArchiveAsync(dialog.FileName, null).ConfigureAwait(true);
        }
    }

    private async Task LoadArchiveAsync(string archivePath, PasswordContext? password)
    {
        if (!File.Exists(archivePath))
        {
            ShowValidation("Choose an existing archive file.");
            return;
        }

        try
        {
            SetBusy(true);
            SetStatus("Reading archive...", 0);
            var result = await Task.Run(() =>
            {
                var info = _archives.Info(archivePath, password);
                var entries = _archives.List(archivePath, password);
                return new ArchiveLoadResult(info, entries);
            }).ConfigureAwait(true);

            _currentArchivePath = Path.GetFullPath(archivePath);
            _currentPassword = password;
            _currentSummary = result.Summary;
            PopulateEntries(result.Entries);
            UpdateArchiveState(_currentArchivePath, result.Summary);
            SetStatus($"Opened {Path.GetFileName(archivePath)}.", 0);
        }
        catch (ArchivePasswordRequiredException) when (password is null)
        {
            var prompted = PromptForPassword("Archive password");
            if (prompted is not null)
            {
                await LoadArchiveAsync(archivePath, prompted).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PopulateEntries(IReadOnlyList<ArchiveEntryListing> entries)
    {
        _entriesView.BeginUpdate();
        try
        {
            _entriesView.Items.Clear();
            foreach (var entry in entries.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                var name = string.IsNullOrWhiteSpace(entry.Path) ? "." : Path.GetFileName(entry.Path.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = entry.Path;
                }

                var item = new ListViewItem(name, entry.IsDirectory ? "folder" : "file");
                item.SubItems.Add(entry.IsDirectory ? string.Empty : FormatBytes(entry.OriginalSize));
                item.SubItems.Add(entry.IsDirectory ? string.Empty : FormatBytes(entry.CompressedSize));
                item.SubItems.Add(entry.IsDirectory ? "Folder" : Path.GetExtension(entry.Path).TrimStart('.').ToUpperInvariant());
                item.SubItems.Add(string.Empty);
                item.SubItems.Add(entry.Method);
                item.SubItems.Add(entry.IsEncrypted ? "Yes" : "No");
                item.SubItems.Add(entry.Path);
                _entriesView.Items.Add(item);
            }
        }
        finally
        {
            _entriesView.EndUpdate();
        }
    }

    private async Task ShowCreateDialogAsync(IEnumerable<string> initialInputs)
    {
        using var dialog = new CreateArchiveDialog(initialInputs);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var options = dialog.CreateOptions;
        await RunOperationAsync(
            "Creating archive...",
            progress => _archives.CompressAsync(dialog.InputPaths, dialog.OutputPath, options, progress),
            $"Created {Path.GetFileName(dialog.OutputPath)}.").ConfigureAwait(true);

        if (File.Exists(dialog.OutputPath))
        {
            await LoadArchiveAsync(dialog.OutputPath, options.Password).ConfigureAwait(true);
        }
    }

    private async Task ShowExtractDialogAsync()
    {
        if (_currentArchivePath is null)
        {
            ShowValidation("Open an archive before extracting.");
            return;
        }

        using var dialog = new ExtractArchiveDialog(_currentArchivePath, _currentPassword);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var options = new ExtractArchiveOptions
        {
            Overwrite = dialog.Overwrite,
            VerifyChecksums = true,
            Password = dialog.Password
        };

        await RunOperationAsync(
            "Extracting archive...",
            progress => _archives.ExtractAsync(_currentArchivePath, dialog.DestinationFolder, options, progress),
            $"Extracted to {dialog.DestinationFolder}.").ConfigureAwait(true);
        _currentPassword = dialog.Password;
    }

    private async Task TestCurrentArchiveAsync()
    {
        if (_currentArchivePath is null)
        {
            ShowValidation("Open an archive before testing.");
            return;
        }

        await RunOperationAsync(
            "Testing archive...",
            async _ =>
            {
                var result = await _archives.TestAsync(_currentArchivePath, _currentPassword).ConfigureAwait(false);
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.Message);
                }
            },
            "Archive integrity OK.").ConfigureAwait(true);
    }

    private void ShowArchiveInfo()
    {
        if (_currentSummary is null || _currentArchivePath is null)
        {
            ShowValidation("Open an archive to view information.");
            return;
        }

        MessageBox.Show(this, FormatSummary(_currentArchivePath, _currentSummary), "Archive information", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task RunOperationAsync(
        string startedMessage,
        Func<IProgress<ArchiveOperationProgress>, Task> operation,
        string completedMessage)
    {
        try
        {
            SetBusy(true);
            SetStatus(startedMessage, 0);
            var progress = new Progress<ArchiveOperationProgress>(p =>
            {
                var percent = Math.Clamp((int)Math.Round(p.Percent), 0, 100);
                SetStatus(string.IsNullOrWhiteSpace(p.CurrentItem) ? startedMessage : p.CurrentItem, percent);
            });

            await operation(progress).ConfigureAwait(true);
            SetStatus(completedMessage, 100);
        }
        catch (ArchivePasswordRequiredException)
        {
            ShowValidation("This archive requires a password.");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateArchiveState(string? archivePath, ArchiveSummary? summary)
    {
        _archivePathText.Text = archivePath ?? string.Empty;
        var hasArchive = archivePath is not null;
        _extractButton.Enabled = hasArchive;
        _testButton.Enabled = hasArchive;
        _infoButton.Enabled = hasArchive;
        _summaryLabel.Text = summary is null
            ? "No archive"
            : $"{summary.FileCount} files, {summary.FolderCount} folders, {FormatBytes(summary.CompressedSize)} packed";
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        foreach (Control control in Controls)
        {
            control.Enabled = !busy;
        }

        if (MainMenuStrip is not null)
        {
            foreach (ToolStripItem item in MainMenuStrip.Items)
            {
                item.Enabled = !busy;
            }
        }

        if (!busy)
        {
            UpdateArchiveState(_currentArchivePath, _currentSummary);
        }

        _progressBar.Visible = true;
    }

    private void SetStatus(string message, int percent)
    {
        _statusLabel.Text = message;
        _progressBar.Value = Math.Clamp(percent, 0, 100);
    }

    private void FocusSearch()
    {
        if (_entriesView.Items.Count == 0)
        {
            return;
        }

        _entriesView.Focus();
        _entriesView.Items[0].Selected = true;
    }

    private PasswordContext? PromptForPassword(string title)
    {
        using var dialog = new PasswordDialog(title);
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Password : null;
    }

    private void ShowValidation(string message)
    {
        SetStatus(message, 0);
        MessageBox.Show(this, message, "Laplace", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowError(Exception ex)
    {
        SetStatus(ex.Message, 0);
        MessageBox.Show(this, ex.Message, "Laplace error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void MainForm_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    private async void MainForm_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        if (paths.Length == 1 && IsArchivePath(paths[0]))
        {
            await LoadArchiveAsync(paths[0], null).ConfigureAwait(true);
            return;
        }

        await ShowCreateDialogAsync(paths).ConfigureAwait(true);
    }

    private async void ApplyStartupArgs(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        var first = args[0];
        if (string.Equals(first, "--add", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            await ShowCreateDialogAsync(args.Skip(1)).ConfigureAwait(true);
            return;
        }

        if (string.Equals(first, "--open", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            first = args[1];
        }

        if (File.Exists(first) && IsArchivePath(first))
        {
            await LoadArchiveAsync(first, null).ConfigureAwait(true);
        }
    }

    private static ToolStripButton CreateToolButton(string text, Icon icon, EventHandler handler)
    {
        var button = new ToolStripButton(text)
        {
            Image = icon.ToBitmap(),
            TextImageRelation = TextImageRelation.ImageAboveText,
            AutoSize = false,
            Size = new Size(72, 52)
        };
        button.Click += handler;
        return button;
    }

    private static bool IsArchivePath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".lpc" or ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".zst" or ".lzip";
    }

    private static string FormatSummary(string archivePath, ArchiveSummary info)
    {
        var version = info.ArchiveVersion > 0 ? $" v{info.ArchiveVersion}" : string.Empty;
        var created = info.CreatedUtc is null ? "Unknown" : info.CreatedUtc.Value.ToLocalTime().ToString("g");
        var methods = info.MethodsUsed.Length == 0 ? "-" : string.Join(", ", info.MethodsUsed);
        var lines = new[]
        {
            $"Archive: {archivePath}",
            $"Format: {info.Format}{version}",
            $"Encrypted: {(info.IsEncrypted ? "Yes" : "No")}",
            $"Files: {info.FileCount}",
            $"Folders: {info.FolderCount}",
            $"Blocks/entries: {info.BlockCount}",
            $"Original size: {FormatBytes(info.OriginalSize)}",
            $"Packed size: {FormatBytes(info.CompressedSize)}",
            $"Ratio: {info.Ratio:P2}",
            $"Created: {created}",
            $"Methods: {methods}",
            info.Notes
        };
        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:F2} {units[unit]}";
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            return null;
        }
    }

    private sealed record ArchiveLoadResult(ArchiveSummary Summary, IReadOnlyList<ArchiveEntryListing> Entries);
}

internal sealed class CreateArchiveDialog : Form
{
    private readonly ListBox _inputs = new();
    private readonly TextBox _output = new();
    private readonly ComboBox _mode = new();
    private readonly ComboBox _blockSize = new();
    private readonly ComboBox _solid = new();
    private readonly NumericUpDown _threads = new();
    private readonly CheckBox _verify = new();
    private readonly CheckBox _encrypt = new();
    private readonly TextBox _password = new();

    public CreateArchiveDialog(IEnumerable<string> initialInputs)
    {
        Text = "Add to archive";
        ClientSize = new Size(720, 500);
        MinimumSize = new Size(640, 430);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            RowCount = 3,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        _inputs.Dock = DockStyle.Fill;
        _inputs.HorizontalScrollbar = true;
        foreach (var path in initialInputs.Where(path => File.Exists(path) || Directory.Exists(path)).Select(Path.GetFullPath))
        {
            _inputs.Items.Add(path);
        }

        var inputPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        buttons.Controls.Add(DialogButton("Add files", AddFiles));
        buttons.Controls.Add(DialogButton("Add folder", AddFolder));
        buttons.Controls.Add(DialogButton("Remove", (_, _) => RemoveSelectedInputs()));
        buttons.Controls.Add(DialogButton("Clear", (_, _) => _inputs.Items.Clear()));
        inputPanel.Controls.Add(_inputs, 0, 0);
        inputPanel.Controls.Add(buttons, 1, 0);

        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        for (var i = 0; i < 7; i++)
        {
            options.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        }

        ConfigureCombo(_mode, ["Balanced", "Fast", "Maximum", "Auto"], 0);
        ConfigureCombo(_blockSize, ["8M", "4M", "16M", "32M", "64M"], 0);
        ConfigureCombo(_solid, ["Auto", "On", "Off"], 0);
        _threads.Minimum = 1;
        _threads.Maximum = Math.Max(1, Environment.ProcessorCount * 2);
        _threads.Value = Math.Max(1, Environment.ProcessorCount);
        _threads.Width = 110;
        _password.UseSystemPasswordChar = true;
        _verify.Text = "Verify after archiving";
        _verify.Checked = true;
        _verify.AutoSize = true;
        _encrypt.Text = "Encrypt";
        _encrypt.AutoSize = true;

        options.Controls.Add(FormLabel("Archive"), 0, 0);
        options.Controls.Add(_output, 1, 0);
        options.Controls.Add(DialogButton("Browse", BrowseOutput), 2, 0);
        options.Controls.Add(FormLabel("Mode"), 0, 1);
        options.Controls.Add(_mode, 1, 1);
        options.Controls.Add(FormLabel("Block size"), 0, 2);
        options.Controls.Add(_blockSize, 1, 2);
        options.Controls.Add(FormLabel("Solid"), 0, 3);
        options.Controls.Add(_solid, 1, 3);
        options.Controls.Add(FormLabel("Threads"), 0, 4);
        options.Controls.Add(_threads, 1, 4);
        options.Controls.Add(FormLabel("Password"), 0, 5);
        options.Controls.Add(_password, 1, 5);
        options.Controls.Add(_verify, 1, 6);
        options.Controls.Add(_encrypt, 2, 6);
        _output.Dock = DockStyle.Fill;
        _password.Dock = DockStyle.Left;
        _password.Width = 220;

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 92 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 92 };
        ok.Click += ValidateBeforeClose;
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        root.Controls.Add(inputPanel, 0, 0);
        root.Controls.Add(options, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
    }

    public string[] InputPaths => _inputs.Items.Cast<string>().ToArray();
    public string OutputPath => _output.Text.Trim();

    public CreateArchiveOptions CreateOptions => new()
    {
        Mode = ParseMode(_mode.Text),
        BlockSizeBytes = ParseBlockSize(_blockSize.Text),
        SolidMode = ParseSolidMode(_solid.Text),
        Threads = (int)_threads.Value,
        VerifyAfterCompression = _verify.Checked,
        Password = PasswordContext.FromNullable(_password.Text)
    };

    private void AddFiles(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Title = "Add files", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddInputs(dialog.FileNames);
        }
    }

    private void AddFolder(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Add folder" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddInputs([dialog.SelectedPath]);
        }
    }

    private void AddInputs(IEnumerable<string> paths)
    {
        var existing = _inputs.Items.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(path => File.Exists(path) || Directory.Exists(path)).Select(Path.GetFullPath))
        {
            if (existing.Add(path))
            {
                _inputs.Items.Add(path);
            }
        }

        if (string.IsNullOrWhiteSpace(_output.Text))
        {
            _output.Text = DefaultArchiveName();
        }
    }

    private void RemoveSelectedInputs()
    {
        foreach (var item in _inputs.SelectedItems.Cast<object>().ToArray())
        {
            _inputs.Items.Remove(item);
        }
    }

    private void BrowseOutput(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Archive name",
            Filter = "Laplace archive (*.lpc)|*.lpc|ZIP archive (*.zip)|*.zip",
            AddExtension = true,
            DefaultExt = "lpc",
            FileName = string.IsNullOrWhiteSpace(_output.Text) ? DefaultArchiveName() : _output.Text
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _output.Text = dialog.FileName;
        }
    }

    private void ValidateBeforeClose(object? sender, EventArgs e)
    {
        if (_inputs.Items.Count == 0)
        {
            ShowDialogMessage("Add at least one file or folder.");
            DialogResult = DialogResult.None;
            return;
        }

        if (string.IsNullOrWhiteSpace(_output.Text))
        {
            ShowDialogMessage("Choose an archive name.");
            DialogResult = DialogResult.None;
            return;
        }

        if (_encrypt.Checked && string.IsNullOrEmpty(_password.Text))
        {
            ShowDialogMessage("Enter a password or turn off encryption.");
            DialogResult = DialogResult.None;
        }
    }

    private string DefaultArchiveName()
    {
        if (_inputs.Items.Count == 1)
        {
            var path = _inputs.Items[0]?.ToString() ?? "archive";
            var directory = Directory.Exists(path)
                ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))
                : Path.GetDirectoryName(path);
            var name = Directory.Exists(path)
                ? new DirectoryInfo(path).Name
                : Path.GetFileNameWithoutExtension(path);
            return Path.Combine(directory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{name}.lpc");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "archive.lpc");
    }

    private static Button DialogButton(string text, EventHandler handler)
    {
        var button = new Button { Text = text, Width = 96, Height = 26, Margin = new Padding(3, 1, 3, 4) };
        button.Click += handler;
        return button;
    }

    private static Label FormLabel(string text)
    {
        return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    }

    private static void ConfigureCombo(ComboBox combo, string[] values, int index)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Items.AddRange(values);
        combo.SelectedIndex = index;
        combo.Width = 170;
    }

    private static CompressionMode ParseMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "fast" => CompressionMode.Fast,
            "maximum" => CompressionMode.Maximum,
            "auto" => CompressionMode.Auto,
            _ => CompressionMode.Balanced
        };
    }

    private static SolidMode ParseSolidMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "on" => SolidMode.On,
            "off" => SolidMode.Off,
            _ => SolidMode.Auto
        };
    }

    private static int ParseBlockSize(string value)
    {
        return int.Parse(value.TrimEnd('M', 'm')) * 1024 * 1024;
    }

    private void ShowDialogMessage(string message)
    {
        MessageBox.Show(this, message, "Laplace", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

internal sealed class ExtractArchiveDialog : Form
{
    private readonly TextBox _destination = new();
    private readonly TextBox _password = new();
    private readonly CheckBox _overwrite = new();

    public ExtractArchiveDialog(string archivePath, PasswordContext? password)
    {
        Text = "Extract";
        ClientSize = new Size(620, 190);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 3,
            RowCount = 5
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        for (var i = 0; i < 4; i++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        }
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var archive = new TextBox { Text = archivePath, ReadOnly = true, Dock = DockStyle.Fill };
        _destination.Text = Path.Combine(Path.GetDirectoryName(archivePath) ?? string.Empty, Path.GetFileNameWithoutExtension(archivePath));
        _destination.Dock = DockStyle.Fill;
        _password.Dock = DockStyle.Left;
        _password.Width = 220;
        _password.UseSystemPasswordChar = true;
        _password.Text = password?.Password ?? string.Empty;
        _overwrite.Text = "Overwrite existing files";
        _overwrite.AutoSize = true;

        root.Controls.Add(FormLabel("Archive"), 0, 0);
        root.Controls.Add(archive, 1, 0);
        root.SetColumnSpan(archive, 2);
        root.Controls.Add(FormLabel("Extract to"), 0, 1);
        root.Controls.Add(_destination, 1, 1);
        root.Controls.Add(DialogButton("Browse", BrowseDestination), 2, 1);
        root.Controls.Add(FormLabel("Password"), 0, 2);
        root.Controls.Add(_password, 1, 2);
        root.Controls.Add(_overwrite, 1, 3);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 92 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 92 };
        ok.Click += ValidateBeforeClose;
        footer.Controls.Add(ok);
        footer.Controls.Add(cancel);
        root.Controls.Add(footer, 0, 4);
        root.SetColumnSpan(footer, 3);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(root);
    }

    public string DestinationFolder => _destination.Text.Trim();
    public bool Overwrite => _overwrite.Checked;
    public PasswordContext? Password => PasswordContext.FromNullable(_password.Text);

    private void BrowseDestination(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose destination folder" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _destination.Text = dialog.SelectedPath;
        }
    }

    private void ValidateBeforeClose(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_destination.Text))
        {
            MessageBox.Show(this, "Choose a destination folder.", "Laplace", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
        }
    }

    private static Button DialogButton(string text, EventHandler handler)
    {
        var button = new Button { Text = text, Width = 86, Height = 26 };
        button.Click += handler;
        return button;
    }

    private static Label FormLabel(string text)
    {
        return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    }
}

internal sealed class PasswordDialog : Form
{
    private readonly TextBox _password = new();

    public PasswordDialog(string title)
    {
        Text = title;
        ClientSize = new Size(360, 118);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 2
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        _password.UseSystemPasswordChar = true;
        _password.Dock = DockStyle.Fill;
        root.Controls.Add(new Label { Text = "Password", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        root.Controls.Add(_password, 1, 0);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.Add(new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 82 });
        footer.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 82 });
        root.Controls.Add(footer, 0, 1);
        root.SetColumnSpan(footer, 2);
        AcceptButton = footer.Controls.OfType<Button>().First();
        CancelButton = footer.Controls.OfType<Button>().Last();
        Controls.Add(root);
    }

    public PasswordContext? Password => PasswordContext.FromNullable(_password.Text);
}
