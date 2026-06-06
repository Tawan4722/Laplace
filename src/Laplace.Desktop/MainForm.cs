using Laplace.Compression;
using Laplace.Core.Enums;
using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using Laplace.Core.Services;
using CompressionMode = Laplace.Core.Enums.CompressionMode;

namespace Laplace.Desktop;

public sealed class MainForm : Form
{
    private readonly CompressorRegistry _compressorRegistry = new();
    private readonly UniversalArchiveService _archives;
    private readonly LpcArchiveMutationService _mutator;
    private readonly TextBox _archivePathText = new();
    private readonly ListView _entriesView = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _summaryLabel = new();
    private readonly ToolStripProgressBar _progressBar = new();
    private readonly ToolStripButton _cancelOperationButton = new();
    private readonly ToolStripButton _extractButton = new();
    private readonly ToolStripButton _testButton = new();
    private readonly ToolStripButton _infoButton = new();

    private string? _currentArchivePath;
    private PasswordContext? _currentPassword;
    private ArchiveSummary? _currentSummary;
    private CancellationTokenSource? _operationCancellation;

    public MainForm(string[] args)
    {
        _archives = new UniversalArchiveService(_compressorRegistry);
        _mutator = new LpcArchiveMutationService(_compressorRegistry);
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
        file.DropDownItems.Add("&Estimate compression...", null, async (_, _) => await ChooseAndEstimateAsync().ConfigureAwait(true));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var commands = new ToolStripMenuItem("&Commands");
        commands.DropDownItems.Add("&Extract to...", null, async (_, _) => await ShowExtractDialogAsync().ConfigureAwait(true));
        commands.DropDownItems.Add("&Test archive", null, async (_, _) => await TestCurrentArchiveAsync().ConfigureAwait(true));
        commands.DropDownItems.Add("&Archive information", null, (_, _) => ShowArchiveInfo());

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("&Extract ISO to removable drive...", null, async (_, _) => await ChooseAndExtractIsoToDriveAsync().ConfigureAwait(true));
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
        toolbar.Items.Add(CreateToolButton("Estimate", SystemIcons.Information, async (_, _) => await ChooseAndEstimateAsync().ConfigureAwait(true)));
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
        toolbar.Items.Add(CreateToolButton("Delete", SystemIcons.Error, async (_, _) => await DeleteSelectedEntriesAsync().ConfigureAwait(true)));
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
        _cancelOperationButton.Text = "Cancel";
        _cancelOperationButton.Enabled = false;
        _cancelOperationButton.Visible = false;
        _cancelOperationButton.Click += (_, _) => CancelCurrentOperation();
        status.Items.AddRange([_statusLabel, _summaryLabel, _progressBar, _cancelOperationButton]);
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
                item.Tag = entry;
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
        var completed = await RunOperationAsync(
            "Creating archive...",
            (progress, cancellationToken) => _archives.CompressAsync(dialog.InputPaths, dialog.OutputPath, options, progress, cancellationToken),
            $"Created {Path.GetFileName(dialog.OutputPath)}.").ConfigureAwait(true);

        if (completed && File.Exists(dialog.OutputPath))
        {
            await LoadArchiveAsync(dialog.OutputPath, options.Password).ConfigureAwait(true);
        }
    }

    private async Task ChooseAndEstimateAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Estimate compression",
            Filter = "All files|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await ShowEstimateAsync(dialog.FileNames).ConfigureAwait(true);
        }
    }

    private async Task ShowEstimateAsync(IEnumerable<string> inputPaths)
    {
        var paths = inputPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (paths.Length == 0)
        {
            ShowValidation("Choose at least one file or folder to estimate.");
            return;
        }

        try
        {
            using var cancellation = new CancellationTokenSource();
            _operationCancellation = cancellation;
            SetBusy(true, canCancel: true);
            SetStatus("Estimating compression...", 0);
            var progress = new Progress<ArchiveOperationProgress>(p =>
            {
                var percent = Math.Clamp((int)Math.Round(p.Percent), 0, 100);
                SetStatus(string.IsNullOrWhiteSpace(p.CurrentItem) ? "Estimating compression..." : p.CurrentItem, percent);
            });

            var estimate = await _archives.EstimateAsync(paths, new CreateArchiveOptions
            {
                Mode = CompressionMode.Auto,
                BlockSizeBytes = 8 * 1024 * 1024,
                VerifyAfterCompression = false
            }, progress, cancellation.Token).ConfigureAwait(true);

            SetStatus("Compression estimate ready.", 100);
            MessageBox.Show(this, FormatEstimate(estimate), "Compression estimate", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation cancelled.", 0);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _operationCancellation = null;
            SetBusy(false);
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
            SelectedEntryIds = GetSelectedEntryIds(),
            Password = dialog.Password
        };
        var selectionText = options.SelectedEntryIds is { Count: > 0 }
            ? $"{options.SelectedEntryIds.Count} selected item(s)"
            : "archive";

        var completed = await RunOperationAsync(
            $"Extracting {selectionText}...",
            (progress, cancellationToken) => _archives.ExtractAsync(_currentArchivePath, dialog.DestinationFolder, options, progress, cancellationToken),
            $"Extracted {selectionText} to {dialog.DestinationFolder}.").ConfigureAwait(true);
        if (completed)
        {
            _currentPassword = dialog.Password;
        }
    }

    private async Task ChooseAndExtractIsoToDriveAsync()
    {
        using var open = new OpenFileDialog
        {
            Title = "Choose ISO image",
            Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*"
        };
        if (open.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await ShowIsoToDriveDialogAsync(open.FileName).ConfigureAwait(true);
    }

    private async Task ShowIsoToDriveDialogAsync(string isoPath)
    {
        if (!File.Exists(isoPath))
        {
            ShowValidation($"ISO image not found: {isoPath}");
            return;
        }

        using var dialog = new IsoToDriveDialog(isoPath);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var driveRoot = dialog.SelectedDriveRoot;
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            ShowValidation("Choose a removable drive.");
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Extract ISO contents to {driveRoot}?\n\nExisting files are {(dialog.Overwrite ? "overwritten when names match" : "left untouched; extraction stops on name conflicts")}. This does not format or raw-write the drive.",
            "Extract ISO to removable drive",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        await RunOperationAsync(
            "Extracting ISO to removable drive...",
            (progress, cancellationToken) => _archives.ExtractAsync(isoPath, driveRoot, new ExtractArchiveOptions
            {
                Overwrite = dialog.Overwrite,
                VerifyChecksums = false
            }, progress, cancellationToken),
            $"Extracted ISO contents to {driveRoot}.").ConfigureAwait(true);
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
            async (_, cancellationToken) =>
            {
                var result = await _archives.TestAsync(_currentArchivePath, _currentPassword, cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.Message);
                }
            },
            "Archive integrity OK.").ConfigureAwait(true);
    }

    private async Task DeleteSelectedEntriesAsync()
    {
        if (_currentArchivePath is null)
        {
            ShowValidation("Open an archive before deleting entries.");
            return;
        }

        if (ArchiveFormatDetector.DetectReadKind(_currentArchivePath) != SupportedArchiveKind.Lpc)
        {
            ShowValidation("Deleting entries is currently supported for LPC archives only.");
            return;
        }

        var selected = _entriesView.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag)
            .OfType<ArchiveEntryListing>()
            .ToArray();
        if (selected.Length == 0)
        {
            ShowValidation("Select one or more archive entries to delete.");
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Delete {selected.Length} selected archive entr{(selected.Length == 1 ? "y" : "ies")}?",
            "Delete archive entries",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        var completed = await RunOperationAsync(
            "Deleting archive entries...",
            (_, cancellationToken) => _mutator.DeleteAsync(_currentArchivePath, selected.Select(x => x.Id.ToString()), new MutateArchiveOptions
            {
                Password = _currentPassword
            }, cancellationToken),
            "Archive entries deleted.").ConfigureAwait(true);
        if (completed)
        {
            await LoadArchiveAsync(_currentArchivePath, _currentPassword).ConfigureAwait(true);
        }
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

    private async Task<bool> RunOperationAsync(
        string startedMessage,
        Func<IProgress<ArchiveOperationProgress>, CancellationToken, Task> operation,
        string completedMessage)
    {
        try
        {
            using var cancellation = new CancellationTokenSource();
            _operationCancellation = cancellation;
            SetBusy(true, canCancel: true);
            SetStatus(startedMessage, 0);
            var progress = new Progress<ArchiveOperationProgress>(p =>
            {
                var percent = Math.Clamp((int)Math.Round(p.Percent), 0, 100);
                SetStatus(string.IsNullOrWhiteSpace(p.CurrentItem) ? startedMessage : p.CurrentItem, percent);
            });

            await operation(progress, cancellation.Token).ConfigureAwait(true);
            SetStatus(completedMessage, 100);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation cancelled.", 0);
            return false;
        }
        catch (ArchivePasswordRequiredException)
        {
            ShowValidation("This archive requires a password.");
            return false;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return false;
        }
        finally
        {
            _operationCancellation = null;
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

    private void SetBusy(bool busy, bool canCancel = false)
    {
        UseWaitCursor = busy;
        foreach (Control control in Controls)
        {
            if (control is StatusStrip)
            {
                continue;
            }

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
        _cancelOperationButton.Visible = busy && canCancel;
        _cancelOperationButton.Enabled = busy && canCancel;
    }

    private void SetStatus(string message, int percent)
    {
        _statusLabel.Text = message;
        _progressBar.Value = Math.Clamp(percent, 0, 100);
    }

    private void CancelCurrentOperation()
    {
        if (_operationCancellation is null || _operationCancellation.IsCancellationRequested)
        {
            return;
        }

        _cancelOperationButton.Enabled = false;
        SetStatus("Cancelling...", _progressBar.Value);
        _operationCancellation.Cancel();
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

    private IReadOnlySet<long>? GetSelectedEntryIds()
    {
        if (_entriesView.SelectedItems.Count == 0)
        {
            return null;
        }

        return _entriesView.SelectedItems
            .Cast<ListViewItem>()
            .Select(item => item.Tag)
            .OfType<ArchiveEntryListing>()
            .Select(entry => entry.Id)
            .ToHashSet();
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

        if (string.Equals(first, "--extract", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            await LoadArchiveAsync(args[1], null).ConfigureAwait(true);
            if (_currentArchivePath is not null)
            {
                await ShowExtractDialogAsync().ConfigureAwait(true);
            }

            return;
        }

        if (string.Equals(first, "--iso-to-drive", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            await ShowIsoToDriveDialogAsync(args[1]).ConfigureAwait(true);
            return;
        }

        if (string.Equals(first, "--estimate", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            await ShowEstimateAsync(args.Skip(1)).ConfigureAwait(true);
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
        return extension is ".lpc" or ".zip" or ".7z" or ".rar" or ".cab" or ".iso" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".zst" or ".lzip";
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
            $"Metadata encrypted: {(info.IsMetadataEncrypted ? "Yes" : "No")}",
            $"Locked: {(info.IsLocked ? "Yes" : "No")}",
            $"Recovery record: {(info.HasRecoveryRecord ? "Yes" : "No")}",
            string.IsNullOrEmpty(info.Comment) ? string.Empty : $"Comment: {info.Comment}",
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

    private static string FormatEstimate(ArchiveEstimate estimate)
    {
        var lines = new[]
        {
            $"Files: {estimate.FileCount}",
            $"Folders: {estimate.FolderCount}",
            $"Original size: {FormatBytes(estimate.OriginalSize)}",
            $"Estimated archive size: {FormatBytes(estimate.EstimatedCompressedSize)}",
            $"Estimated ratio: {estimate.EstimatedRatio:P2}",
            $"Estimated reduction: {estimate.EstimatedReduction:P2}",
            $"Confidence: {estimate.Confidence}",
            $"Likely methods: {string.Join(", ", estimate.LikelyMethods)}",
            estimate.Notes
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
    private readonly ComboBox _volumeSize = new();
    private readonly NumericUpDown _threads = new();
    private readonly CheckBox _verify = new();
    private readonly CheckBox _encrypt = new();
    private readonly CheckBox _hideNames = new();
    private readonly NumericUpDown _recovery = new();
    private readonly TextBox _password = new();
    private readonly TextBox _confirmPassword = new();
    private bool _blockSizeChanged;
    private bool _autoOutputPath = true;
    private bool _updatingOutputPath;

    public CreateArchiveDialog(IEnumerable<string> initialInputs)
    {
        Text = "Add to archive";
        ClientSize = new Size(720, 612);
        MinimumSize = new Size(640, 460);
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 322));
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
        buttons.Controls.Add(DialogButton("Clear", (_, _) =>
        {
            _inputs.Items.Clear();
            UpdateDefaultOutputPath();
        }));
        inputPanel.Controls.Add(_inputs, 0, 0);
        inputPanel.Controls.Add(buttons, 1, 0);

        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        for (var i = 0; i < 11; i++)
        {
            options.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        }

        ConfigureCombo(_mode, ["Balanced", "Fast", "Maximum", "Intensive", "Compressed", "Extreme", "Auto"], 0);
        ConfigureCombo(_blockSize, ["8M", "4M", "16M", "32M", "64M"], 0);
        ConfigureCombo(_solid, ["Auto", "On", "Off"], 0);
        ConfigureCombo(_volumeSize, ["None", "10M", "100M", "700M", "1G", "4G"], 0);
        _threads.Minimum = 1;
        _threads.Maximum = Math.Max(1, Environment.ProcessorCount * 2);
        _threads.Value = Math.Max(1, Environment.ProcessorCount);
        _threads.Width = 110;
        _password.UseSystemPasswordChar = true;
        _confirmPassword.UseSystemPasswordChar = true;
        _verify.Text = "Verify after archiving";
        _verify.Checked = true;
        _verify.AutoSize = true;
        _encrypt.Text = "Encrypt";
        _encrypt.AutoSize = true;
        _hideNames.Text = "Hide names";
        _hideNames.AutoSize = true;
        _hideNames.CheckedChanged += (_, _) =>
        {
            if (_hideNames.Checked)
            {
                _encrypt.Checked = true;
            }
        };
        _recovery.Minimum = 0;
        _recovery.Maximum = 100;
        _recovery.Width = 110;

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
        options.Controls.Add(FormLabel("Confirm"), 0, 6);
        options.Controls.Add(_confirmPassword, 1, 6);
        options.Controls.Add(FormLabel("Recovery %"), 0, 7);
        options.Controls.Add(_recovery, 1, 7);
        options.Controls.Add(FormLabel("Volume size"), 0, 8);
        options.Controls.Add(_volumeSize, 1, 8);
        options.Controls.Add(_verify, 1, 9);
        options.Controls.Add(_encrypt, 2, 9);
        options.Controls.Add(_hideNames, 2, 10);
        _output.Dock = DockStyle.Fill;
        _password.Dock = DockStyle.Left;
        _password.Width = 220;
        _confirmPassword.Dock = DockStyle.Left;
        _confirmPassword.Width = 220;
        _output.TextChanged += (_, _) =>
        {
            if (!_updatingOutputPath)
            {
                _autoOutputPath = false;
            }
        };
        _blockSize.SelectedIndexChanged += (_, _) => _blockSizeChanged = true;
        _mode.SelectedIndexChanged += (_, _) =>
        {
            _blockSize.Enabled = !_mode.Text.Equals("Extreme", StringComparison.OrdinalIgnoreCase);
        };
        UpdateDefaultOutputPath();

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
        BlockSizeExplicitlySet = _blockSizeChanged && !_mode.Text.Equals("Extreme", StringComparison.OrdinalIgnoreCase),
        SolidMode = ParseSolidMode(_solid.Text),
        Threads = (int)_threads.Value,
        VerifyAfterCompression = _verify.Checked,
        Password = _encrypt.Checked ? PasswordContext.FromNullable(_password.Text) : null,
        EncryptMetadata = _hideNames.Checked,
        RecoveryPercent = (int)_recovery.Value,
        VolumeSizeBytes = ParseVolumeSize(_volumeSize.Text)
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

        UpdateDefaultOutputPath();
    }

    private void RemoveSelectedInputs()
    {
        foreach (var item in _inputs.SelectedItems.Cast<object>().ToArray())
        {
            _inputs.Items.Remove(item);
        }

        UpdateDefaultOutputPath();
    }

    private void BrowseOutput(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Archive name",
            Filter = "Laplace archive (*.lpc)|*.lpc|ZIP archive (*.zip)|*.zip|7-Zip archive (*.7z)|*.7z|WinRAR archive (*.rar)|*.rar",
            AddExtension = true,
            DefaultExt = "lpc",
            FileName = string.IsNullOrWhiteSpace(_output.Text) ? DefaultArchiveName() : _output.Text
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _autoOutputPath = false;
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
            return;
        }

        if ((_hideNames.Checked || _recovery.Value > 0) &&
            !Path.GetExtension(_output.Text).Equals(".lpc", StringComparison.OrdinalIgnoreCase))
        {
            ShowDialogMessage("Metadata encryption and recovery records require an LPC archive.");
            DialogResult = DialogResult.None;
            return;
        }

        var outputExtension = Path.GetExtension(_output.Text);
        if (!_volumeSize.Text.Equals("None", StringComparison.OrdinalIgnoreCase) &&
            !outputExtension.Equals(".7z", StringComparison.OrdinalIgnoreCase) &&
            !outputExtension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            ShowDialogMessage("Multi-volume output requires a 7z or RAR archive.");
            DialogResult = DialogResult.None;
            return;
        }

        if (_encrypt.Checked)
        {
            try
            {
                ArchivePasswordPolicy.EnsureConfirmationMatches(_password.Text, _confirmPassword.Text);
            }
            catch (ArgumentException ex)
            {
                ShowDialogMessage(ex.Message);
                DialogResult = DialogResult.None;
            }
        }
    }

    private string DefaultArchiveName()
    {
        return ArchivePathHelper.ResolveDefaultArchivePath(
            _inputs.Items.Cast<string>(),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }

    private void UpdateDefaultOutputPath()
    {
        if (!_autoOutputPath)
        {
            return;
        }

        _updatingOutputPath = true;
        try
        {
            _output.Text = DefaultArchiveName();
        }
        finally
        {
            _updatingOutputPath = false;
        }
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
            "intensive" => CompressionMode.Intensive,
            "compressed" => CompressionMode.Compressed,
            "extreme" => CompressionMode.Extreme,
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

    private static long? ParseVolumeSize(string value)
    {
        if (value.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suffix = char.ToUpperInvariant(value[^1]);
        var amount = long.Parse(value[..^1]);
        return suffix switch
        {
            'M' => amount * 1024 * 1024,
            'G' => amount * 1024 * 1024 * 1024,
            _ => throw new ArgumentException($"Unsupported volume size: {value}")
        };
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

internal sealed class IsoToDriveDialog : Form
{
    private readonly ComboBox _drives = new();
    private readonly CheckBox _overwrite = new();

    public IsoToDriveDialog(string isoPath)
    {
        Text = "Extract ISO to removable drive";
        ClientSize = new Size(520, 172);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 4
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var image = new TextBox
        {
            Text = isoPath,
            ReadOnly = true,
            Dock = DockStyle.Fill
        };
        _drives.DropDownStyle = ComboBoxStyle.DropDownList;
        _drives.Dock = DockStyle.Fill;
        _overwrite.Text = "Overwrite existing files with matching names";
        _overwrite.AutoSize = true;

        root.Controls.Add(DialogLabel("ISO image"), 0, 0);
        root.Controls.Add(image, 1, 0);
        root.Controls.Add(DialogLabel("Drive"), 0, 1);
        root.Controls.Add(_drives, 1, 1);
        root.Controls.Add(_overwrite, 1, 2);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.Add(new Button { Text = "Extract", DialogResult = DialogResult.OK, Width = 88 });
        footer.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 82 });
        root.Controls.Add(footer, 0, 3);
        root.SetColumnSpan(footer, 2);
        AcceptButton = footer.Controls.OfType<Button>().First();
        CancelButton = footer.Controls.OfType<Button>().Last();

        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Removable && x.IsReady))
        {
            _drives.Items.Add(new DriveChoice(drive));
        }

        if (_drives.Items.Count > 0)
        {
            _drives.SelectedIndex = 0;
        }
        else
        {
            _drives.Items.Add("No ready removable drives found");
            _drives.SelectedIndex = 0;
            footer.Controls.OfType<Button>().First().Enabled = false;
        }

        Controls.Add(root);
    }

    public string SelectedDriveRoot => _drives.SelectedItem is DriveChoice choice ? choice.Root : string.Empty;
    public bool Overwrite => _overwrite.Checked;

    private static Label DialogLabel(string text)
    {
        return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
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

    private sealed class DriveChoice
    {
        public DriveChoice(DriveInfo drive)
        {
            Root = drive.RootDirectory.FullName;
            var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Removable drive" : drive.VolumeLabel;
            Display = $"{label} ({Root}) - {FormatBytes(drive.AvailableFreeSpace)} free";
        }

        public string Root { get; }
        private string Display { get; }
        public override string ToString() => Display;
    }
}
