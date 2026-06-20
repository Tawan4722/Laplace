using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Laplace.Compression;
using Laplace.Core.Enums;
using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using Laplace.Core.Services;
using CompressionMode = Laplace.Core.Enums.CompressionMode;

namespace Laplace.Desktop;

public partial class MainWindow : Window
{
    private readonly CompressorRegistry _compressorRegistry = new();
    private readonly UniversalArchiveService _archives;
    private readonly LpcArchiveMutationService _mutator;

    private string? _currentArchivePath;
    private PasswordContext? _currentPassword;
    private ArchiveSummary? _currentSummary;
    private CancellationTokenSource? _operationCancellation;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _archives = new UniversalArchiveService(_compressorRegistry);
        _mutator = new LpcArchiveMutationService(_compressorRegistry);
        
        UpdateArchiveState(null, null);
        Loaded += (_, _) => ApplyStartupArgs(args);
    }

    private async void OpenArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open archive",
            Filter = "Archive files|*.lpc;*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.zst;*.lzip|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            await LoadArchiveAsync(dialog.FileName, null);
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
            });

            _currentArchivePath = Path.GetFullPath(archivePath);
            _currentPassword = password;
            _currentSummary = result.Summary;
            PopulateEntries(result.Entries);
            UpdateArchiveState(_currentArchivePath, result.Summary);
            SetStatus($"Opened {Path.GetFileName(archivePath)}.", 0);
        }
        catch (ArchivePasswordRequiredException) when (password is null)
        {
            SetBusy(false);
            var prompted = PromptForPassword("Unlock archive", archivePath, "This archive is encrypted. Enter its password to view the contents.");
            if (prompted is not null)
            {
                await LoadArchiveAsync(archivePath, prompted);
            }
            else
            {
                SetStatus("Archive opening cancelled.", 0);
            }
        }
        catch (ArchivePasswordException) when (password is not null)
        {
            SetBusy(false);
            var prompted = PromptForPassword("Try password again", archivePath, "That password could not unlock the archive. Check it and try again.", true);
            if (prompted is not null)
            {
                await LoadArchiveAsync(archivePath, prompted);
            }
            else
            {
                SetStatus("Archive opening cancelled.", 0);
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
        var items = entries.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(entry =>
        {
            var name = string.IsNullOrWhiteSpace(entry.Path) ? "." : Path.GetFileName(entry.Path.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) name = entry.Path;
            return new EntryViewModel
            {
                Name = name,
                Size = entry.IsDirectory ? string.Empty : FormatBytes(entry.OriginalSize),
                Packed = entry.IsDirectory ? string.Empty : FormatBytes(entry.CompressedSize),
                Type = entry.IsDirectory ? "Folder" : Path.GetExtension(entry.Path).TrimStart('.').ToUpperInvariant(),
                Method = entry.Method,
                Encrypted = entry.IsEncrypted ? "Yes" : "No",
                Path = entry.Path,
                EntryListing = entry
            };
        }).ToList();

        EntriesView.ItemsSource = items;
    }

    private async void CreateArchive_Click(object sender, RoutedEventArgs e)
    {
        await ShowCreateDialogAsync(Array.Empty<string>());
    }

    private async Task ShowCreateDialogAsync(IEnumerable<string> initialInputs)
    {
        var dialog = new CreateArchiveDialog(initialInputs) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var options = dialog.CreateOptions;
        var completed = await RunOperationAsync(
            "Creating archive...",
            (progress, cancellationToken) => _archives.CompressAsync(dialog.InputPaths, dialog.OutputPath, options, progress, cancellationToken),
            $"Created {Path.GetFileName(dialog.OutputPath)}.");

        if (completed && File.Exists(dialog.OutputPath))
        {
            await LoadArchiveAsync(dialog.OutputPath, options.Password);
        }
    }

    private async void Estimate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Estimate compression",
            Filter = "All files|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            await ShowEstimateAsync(dialog.FileNames);
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
                var statusText = FormatProgressStatus("Estimating compression...", p);
                SetStatus(statusText, percent);
            });

            var estimate = await _archives.EstimateAsync(paths, new CreateArchiveOptions
            {
                Mode = CompressionMode.Auto,
                BlockSizeBytes = 8 * 1024 * 1024,
                VerifyAfterCompression = false
            }, progress, cancellation.Token);

            SetStatus("Compression estimate ready.", 100);
            MessageBox.Show(this, FormatEstimate(estimate), "Compression estimate", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        await ShowExtractDialogAsync();
    }

    private async Task ShowExtractDialogAsync()
    {
        if (_currentArchivePath is null)
        {
            ShowValidation("Open an archive before extracting.");
            return;
        }

        var dialog = new ExtractArchiveDialog(_currentArchivePath, _currentPassword) { Owner = this };
        if (dialog.ShowDialog() != true) return;

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
            $"Extracted {selectionText} to {dialog.DestinationFolder}.");

        if (completed)
        {
            _currentPassword = dialog.Password;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
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
                var result = await _archives.TestAsync(_currentArchivePath, _currentPassword, cancellationToken);
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.Message);
                }
            },
            "Archive integrity OK.");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
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

        var selectedIds = GetSelectedEntryIds();
        if (selectedIds == null || selectedIds.Count == 0)
        {
            ShowValidation("Select one or more archive entries to delete.");
            return;
        }

        var result = MessageBox.Show(this, $"Delete {selectedIds.Count} selected archive entr{(selectedIds.Count == 1 ? "y" : "ies")}?", "Delete archive entries", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var completed = await RunOperationAsync(
            "Deleting archive entries...",
            (_, cancellationToken) => _mutator.DeleteAsync(_currentArchivePath, selectedIds.Select(id => id.ToString()), new MutateArchiveOptions { Password = _currentPassword }, cancellationToken),
            "Archive entries deleted.");

        if (completed)
        {
            await LoadArchiveAsync(_currentArchivePath, _currentPassword);
        }
    }

    private void Info_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSummary is null || _currentArchivePath is null)
        {
            ShowValidation("Open an archive to view information.");
            return;
        }

        MessageBox.Show(this, FormatSummary(_currentArchivePath, _currentSummary), "Archive information", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void IsoToDrive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose ISO image",
            Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            await ShowIsoToDriveDialogAsync(dialog.FileName);
        }
    }

    private async Task ShowIsoToDriveDialogAsync(string isoPath)
    {
        if (!File.Exists(isoPath))
        {
            ShowValidation($"ISO image not found: {isoPath}");
            return;
        }

        var dialog = new IsoToDriveDialog(isoPath) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var driveRoot = dialog.SelectedDriveRoot;
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            ShowValidation("Choose a removable drive.");
            return;
        }

        var result = MessageBox.Show(this, $"Extract ISO contents to {driveRoot}?\n\nExisting files are {(dialog.Overwrite ? "overwritten when names match" : "left untouched; extraction stops on name conflicts")}. This does not format or raw-write the drive.", "Extract ISO", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        await RunOperationAsync(
            "Extracting ISO to removable drive...",
            (progress, cancellationToken) => _archives.ExtractAsync(isoPath, driveRoot, new ExtractArchiveOptions
            {
                Overwrite = dialog.Overwrite,
                VerifyChecksums = false
            }, progress, cancellationToken),
            $"Extracted ISO contents to {driveRoot}.");
    }

    private void ClearPassword_Click(object sender, RoutedEventArgs e)
    {
        _currentPassword = null;
        SetStatus("Password cleared.", 0);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Laplace archive manager\nNative .lpc and common archive support.", "About Laplace", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void EntriesView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_currentArchivePath is not null)
        {
            await ShowExtractDialogAsync();
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                if (files.Length == 1 && IsArchivePath(files[0]))
                {
                    _ = LoadArchiveAsync(files[0], null);
                }
                else
                {
                    _ = ShowCreateDialogAsync(files);
                }
            }
        }
    }

    private async void ApplyStartupArgs(string[] args)
    {
        if (LpcSfxHelper.IsRunningAsSfx)
        {
            Visibility = Visibility.Hidden;
            Width = 0;
            Height = 0;
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;

            var currentProcessPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(currentProcessPath))
            {
                await LoadArchiveAsync(currentProcessPath, null);
                if (_currentArchivePath is not null)
                {
                    await ShowExtractDialogAsync();
                }
            }
            Close();
            return;
        }

        if (args.Length == 0) return;

        var first = args[0];
        if (string.Equals(first, "--add", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            await ShowCreateDialogAsync(args.Skip(1));
            return;
        }
        if (string.Equals(first, "--extract", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            await LoadArchiveAsync(args[1], null);
            if (_currentArchivePath is not null)
            {
                await ShowExtractDialogAsync();
            }
            return;
        }
        if (string.Equals(first, "--iso-to-drive", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            await ShowIsoToDriveDialogAsync(args[1]);
            return;
        }
        if (string.Equals(first, "--estimate", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            await ShowEstimateAsync(args.Skip(1));
            return;
        }
        if (string.Equals(first, "--extract-here", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            var archivePath = Path.GetFullPath(args[1]);
            var targetDir = Path.GetDirectoryName(archivePath) ?? Directory.GetCurrentDirectory();
            await LoadArchiveAsync(archivePath, null);
            if (_currentArchivePath is not null)
            {
                var options = new ExtractArchiveOptions
                {
                    Overwrite = false,
                    VerifyChecksums = true,
                    Password = _currentPassword
                };
                var completed = await RunOperationAsync(
                    "Extracting archive...",
                    (progress, cancellationToken) => _archives.ExtractAsync(_currentArchivePath, targetDir, options, progress, cancellationToken),
                    $"Extracted to {targetDir}.");
                if (completed)
                {
                    Close();
                }
            }
            else
            {
                Close();
            }
            return;
        }
        if (string.Equals(first, "--extract-to-named-folder", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            var archivePath = Path.GetFullPath(args[1]);
            var folder = Path.Combine(Path.GetDirectoryName(archivePath) ?? Directory.GetCurrentDirectory(), Path.GetFileNameWithoutExtension(archivePath));
            await LoadArchiveAsync(archivePath, null);
            if (_currentArchivePath is not null)
            {
                var options = new ExtractArchiveOptions
                {
                    Overwrite = false,
                    VerifyChecksums = true,
                    Password = _currentPassword
                };
                var completed = await RunOperationAsync(
                    "Extracting archive...",
                    (progress, cancellationToken) => _archives.ExtractAsync(_currentArchivePath, folder, options, progress, cancellationToken),
                    $"Extracted to {folder}.");
                if (completed)
                {
                    Close();
                }
            }
            else
            {
                Close();
            }
            return;
        }
        if (string.Equals(first, "--test", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            var archivePath = Path.GetFullPath(args[1]);
            await LoadArchiveAsync(archivePath, null);
            if (_currentArchivePath is not null)
            {
                await RunOperationAsync(
                    "Testing archive...",
                    async (_, cancellationToken) =>
                    {
                        var result = await _archives.TestAsync(_currentArchivePath, _currentPassword, cancellationToken);
                        if (!result.Success)
                        {
                            throw new InvalidOperationException(result.Message);
                        }
                    },
                    "Archive integrity OK.");
            }
            Close();
            return;
        }
        if (string.Equals(first, "--info", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            var archivePath = Path.GetFullPath(args[1]);
            await LoadArchiveAsync(archivePath, null);
            if (_currentSummary is not null && _currentArchivePath is not null)
            {
                MessageBox.Show(this, FormatSummary(_currentArchivePath, _currentSummary), "Archive information", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Close();
            return;
        }
        if (string.Equals(first, "--repair", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            var archivePath = Path.GetFullPath(args[1]);
            if (File.Exists(archivePath))
            {
                var completed = await RunOperationAsync(
                    "Repairing archive...",
                    async (_, cancellationToken) =>
                    {
                        if (ArchiveFormatDetector.DetectReadKind(archivePath) == SupportedArchiveKind.Rar)
                        {
                            var rarTools = new RarToolCommandService();
                            await rarTools.RepairAsync(archivePath, cancellationToken);
                        }
                        else
                        {
                            var recovery = new LpcRecoveryService();
                            await recovery.RepairAsync(archivePath, cancellationToken);
                        }
                    },
                    "Repair operation completed.");
                if (completed)
                {
                    if (ArchiveFormatDetector.DetectReadKind(archivePath) == SupportedArchiveKind.Rar)
                    {
                        MessageBox.Show(this, "RAR repair completed.", "Repair archive", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(this, "LPC repair completed. Archive has been repaired.", "Repair archive", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            Close();
            return;
        }
        if (string.Equals(first, "--compress-beside", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            var inputPath = Path.GetFullPath(args[1]);
            var mode = CompressionMode.Balanced;
            var verify = false;
            for (var i = 2; i < args.Length; i++)
            {
                if (args[i].Equals("--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var modeStr = args[++i];
                    mode = modeStr.ToLowerInvariant() switch
                    {
                        "fast" => CompressionMode.Fast,
                        "balanced" => CompressionMode.Balanced,
                        "maximum" => CompressionMode.Maximum,
                        "intensive" => CompressionMode.Intensive,
                        "compressed" or "ultra" => CompressionMode.Compressed,
                        "extreme" => CompressionMode.Extreme,
                        "auto" => CompressionMode.Auto,
                        _ => CompressionMode.Balanced
                    };
                }
                else if (args[i].Equals("--verify", StringComparison.OrdinalIgnoreCase))
                {
                    verify = true;
                }
            }
            var outputPath = ArchivePathHelper.ResolveBesideArchivePath(inputPath);
            var options = new CreateArchiveOptions
            {
                Mode = mode,
                VerifyAfterCompression = verify
            };
            if (mode == CompressionMode.Extreme)
            {
                options.AvailableCompressionMemoryBytes = 256L * 1024 * 1024;
            }
            var completed = await RunOperationAsync(
                "Creating archive...",
                (progress, cancellationToken) => _archives.CompressAsync([inputPath], outputPath, options, progress, cancellationToken),
                $"Created {Path.GetFileName(outputPath)}.");
            if (completed)
            {
                Close();
            }
            else
            {
                Close();
            }
            return;
        }
        if (string.Equals(first, "--open", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            first = args[1];
        }

        if (File.Exists(first) && IsArchivePath(first))
        {
            await LoadArchiveAsync(first, null);
        }
    }

    private async Task<bool> RunOperationAsync(string startedMessage, Func<IProgress<ArchiveOperationProgress>, CancellationToken, Task> operation, string completedMessage)
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
                var statusText = FormatProgressStatus(startedMessage, p);
                SetStatus(statusText, percent);
            });

            await operation(progress, cancellation.Token);
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

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is null || _operationCancellation.IsCancellationRequested) return;

        CancelButton.IsEnabled = false;
        OverlayCancelButton.IsEnabled = false;
        SetStatus("Cancelling...", (int)OperationProgress.Value);
        OverlayMessage.Text = "Cancelling...";
        _operationCancellation.Cancel();
    }

    private IReadOnlySet<long>? GetSelectedEntryIds()
    {
        var items = EntriesView.SelectedItems.Cast<EntryViewModel>().ToList();
        if (items.Count == 0) return null;
        return items.Select(i => i.EntryListing.Id).ToHashSet();
    }

    private void UpdateArchiveState(string? archivePath, ArchiveSummary? summary)
    {
        ArchivePathText.Text = archivePath ?? string.Empty;
        var hasArchive = archivePath is not null;
        SummaryLabel.Text = summary is null
            ? "No archive"
            : $"{summary.FileCount} files, {summary.FolderCount} folders, {FormatBytes(summary.CompressedSize)} packed";
    }

    private void SetBusy(bool busy, bool canCancel = false)
    {
        OperationOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OperationProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy && canCancel ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy && canCancel;
        OverlayCancelButton.IsEnabled = busy && canCancel;
        
        if (!busy) UpdateArchiveState(_currentArchivePath, _currentSummary);
    }

    private void SetStatus(string message, int percent)
    {
        StatusLabel.Text = message;
        OperationProgress.Value = Math.Clamp(percent, 0, 100);
        OverlayMessage.Text = message;
        OverlayProgress.Value = Math.Clamp(percent, 0, 100);
    }

    private PasswordContext? PromptForPassword(string title, string archivePath, string description, bool isError = false)
    {
        var dialog = new PasswordDialog(title, archivePath, description, isError) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    private void ShowValidation(string message)
    {
        SetStatus(message, 0);
        MessageBox.Show(this, message, "Laplace", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowError(Exception ex)
    {
        SetStatus(ex.Message, 0);
        MessageBox.Show(this, ex.Message, "Laplace error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static bool IsArchivePath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".lpc" or ".zip" or ".7z" or ".rar" or ".cab" or ".iso" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".zst" or ".lzip";
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

    private static string FormatProgressStatus(string startedMessage, ArchiveOperationProgress p)
    {
        var item = string.IsNullOrWhiteSpace(p.CurrentItem) ? startedMessage : p.CurrentItem;
        var percent = Math.Clamp((int)Math.Round(p.Percent), 0, 100);
        
        if (p.TotalBytes > 0)
        {
            var processedStr = FormatBytes(p.ProcessedBytes);
            var totalStr = FormatBytes(p.TotalBytes);
            return $"{item} ({percent}% - {processedStr} / {totalStr})";
        }
        
        return $"{item} ({percent}%)";
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

    private sealed record ArchiveLoadResult(ArchiveSummary Summary, IReadOnlyList<ArchiveEntryListing> Entries);
}

public class EntryViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string Packed { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Encrypted { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public ArchiveEntryListing EntryListing { get; set; } = null!;
}
