using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using Laplace.Core.Models;
using Laplace.Core.Enums;
using Laplace.Core.Services;
using Laplace.Compression;

namespace Laplace.Desktop;

public partial class CreateArchiveDialog : Window
{
    private bool _blockSizeChanged;
    private bool _autoOutputPath = true;
    private bool _updatingOutputPath;

    public CreateArchiveDialog(IEnumerable<string> initialInputs)
    {
        InitializeComponent();

        ModeCombo.ItemsSource = new[] { "Balanced", "Fast", "Maximum", "Intensive", "Compressed", "Extreme", "Auto" };
        ModeCombo.SelectedIndex = 0;

        BlockSizeCombo.ItemsSource = new[] { "8M", "4M", "16M", "32M", "64M" };
        BlockSizeCombo.SelectedIndex = 0;

        SolidCombo.ItemsSource = new[] { "Auto", "On", "Off" };
        SolidCombo.SelectedIndex = 0;

        VolumeSizeCombo.ItemsSource = new[] { "None", "10M", "100M", "700M", "1G", "4G" };
        VolumeSizeCombo.SelectedIndex = 0;

        ThreadsInput.Text = Math.Max(1, Environment.ProcessorCount).ToString();
        RecoveryInput.Text = "0";

        foreach (var path in initialInputs.Where(path => File.Exists(path) || Directory.Exists(path)).Select(Path.GetFullPath))
        {
            InputList.Items.Add(path);
        }

        UpdateDefaultOutputPath();
    }

    public string[] InputPaths => InputList.Items.Cast<string>().ToArray();
    public string OutputPath => OutputInput.Text.Trim();

    public CreateArchiveOptions CreateOptions => new()
    {
        Mode = ParseMode(ModeCombo.SelectedItem?.ToString() ?? "Balanced"),
        BlockSizeBytes = ParseBlockSize(BlockSizeCombo.SelectedItem?.ToString() ?? "8M"),
        BlockSizeExplicitlySet = _blockSizeChanged && !(ModeCombo.SelectedItem?.ToString()?.Equals("Extreme", StringComparison.OrdinalIgnoreCase) ?? false),
        SolidMode = ParseSolidMode(SolidCombo.SelectedItem?.ToString() ?? "Auto"),
        Threads = int.TryParse(ThreadsInput.Text, out var t) ? t : 1,
        VerifyAfterCompression = VerifyCheck.IsChecked == true,
        Password = EncryptCheck.IsChecked == true ? PasswordContext.FromNullable(PasswordInput.Password) : null,
        EncryptMetadata = HideNamesCheck.IsChecked == true,
        RecoveryPercent = int.TryParse(RecoveryInput.Text, out var r) ? r : 0,
        VolumeSizeBytes = ParseVolumeSize(VolumeSizeCombo.SelectedItem?.ToString() ?? "None")
    };

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Add files", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog() == true)
        {
            AddInputs(dialog.FileNames);
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add folder" };
        if (dialog.ShowDialog() == true)
        {
            AddInputs(new[] { dialog.FolderName });
        }
    }

    private void AddInputs(IEnumerable<string> paths)
    {
        var existing = InputList.Items.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(path => File.Exists(path) || Directory.Exists(path)).Select(Path.GetFullPath))
        {
            if (existing.Add(path))
            {
                InputList.Items.Add(path);
            }
        }
        UpdateDefaultOutputPath();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = InputList.SelectedItems.Cast<object>().ToList();
        foreach (var item in selected)
        {
            InputList.Items.Remove(item);
        }
        UpdateDefaultOutputPath();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        InputList.Items.Clear();
        UpdateDefaultOutputPath();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Archive name",
            Filter = "Laplace archive (*.lpc)|*.lpc|ZIP archive (*.zip)|*.zip|7-Zip archive (*.7z)|*.7z|WinRAR archive (*.rar)|*.rar",
            AddExtension = true,
            DefaultExt = "lpc",
            FileName = string.IsNullOrWhiteSpace(OutputInput.Text) ? DefaultArchiveName() : OutputInput.Text
        };

        if (dialog.ShowDialog() == true)
        {
            _autoOutputPath = false;
            OutputInput.Text = dialog.FileName;
        }
    }

    private void OutputInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_updatingOutputPath)
        {
            _autoOutputPath = false;
        }
    }

    private void BlockSizeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _blockSizeChanged = true;
    }

    private void ModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (BlockSizeCombo != null && ModeCombo.SelectedItem != null)
        {
            BlockSizeCombo.IsEnabled = !(ModeCombo.SelectedItem.ToString() ?? "").Equals("Extreme", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void HideNamesCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (HideNamesCheck.IsChecked == true && EncryptCheck != null)
        {
            EncryptCheck.IsChecked = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (InputList.Items.Count == 0)
        {
            MessageBox.Show(this, "Add at least one file or folder.", "Laplace", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputInput.Text))
        {
            MessageBox.Show(this, "Choose an archive name.", "Laplace", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (EncryptCheck.IsChecked == true && string.IsNullOrEmpty(PasswordInput.Password))
        {
            MessageBox.Show(this, "Enter a password or turn off encryption.", "Laplace", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if ((HideNamesCheck.IsChecked == true || (int.TryParse(RecoveryInput.Text, out var r) && r > 0)) &&
            !Path.GetExtension(OutputInput.Text).Equals(".lpc", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Metadata encryption and recovery records require an LPC archive.", "Laplace", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var outputExtension = Path.GetExtension(OutputInput.Text);
        if (!(VolumeSizeCombo.SelectedItem?.ToString() ?? "None").Equals("None", StringComparison.OrdinalIgnoreCase) &&
            !outputExtension.Equals(".lpc", StringComparison.OrdinalIgnoreCase) &&
            !outputExtension.Equals(".7z", StringComparison.OrdinalIgnoreCase) &&
            !outputExtension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Multi-volume output requires an LPC, 7z, or RAR archive.", "Laplace", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (EncryptCheck.IsChecked == true)
        {
            try
            {
                ArchivePasswordPolicy.EnsureConfirmationMatches(PasswordInput.Password, ConfirmPasswordInput.Password);
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(this, ex.Message, "Laplace", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        DialogResult = true;
        Close();
    }

    private string DefaultArchiveName()
    {
        return ArchivePathHelper.ResolveDefaultArchivePath(
            InputList.Items.Cast<string>(),
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
            OutputInput.Text = DefaultArchiveName();
        }
        finally
        {
            _updatingOutputPath = false;
        }
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
}
