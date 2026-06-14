using System.Windows;
using System.IO;
using Laplace.Core.Models;
using Microsoft.Win32;

namespace Laplace.Desktop;

public partial class ExtractArchiveDialog : Window
{
    public string DestinationFolder => DestinationInput.Text.Trim();
    public bool Overwrite => OverwriteCheck.IsChecked == true;
    public PasswordContext? Password => PasswordContext.FromNullable(PasswordInput.Password);

    public ExtractArchiveDialog(string archivePath, PasswordContext? password)
    {
        InitializeComponent();
        ArchiveInput.Text = archivePath;
        DestinationInput.Text = Path.Combine(Path.GetDirectoryName(archivePath) ?? string.Empty, Path.GetFileNameWithoutExtension(archivePath));
        if (password != null && !string.IsNullOrEmpty(password.Password))
        {
            PasswordInput.Password = password.Password;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose destination folder" };
        if (dialog.ShowDialog() == true)
        {
            DestinationInput.Text = dialog.FolderName;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DestinationInput.Text))
        {
            System.Windows.MessageBox.Show(this, "Choose a destination folder.", "Laplace", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
        Close();
    }
}
