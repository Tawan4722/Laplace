using System.Windows;
using Laplace.Core.Models;

namespace Laplace.Desktop;

public partial class PasswordDialog : Window
{
    public PasswordContext? Password { get; private set; }

    public PasswordDialog(string title, string archivePath, string description, bool isError = false)
    {
        InitializeComponent();
        Title = title;
        HeaderTextBlock.Text = isError ? "Password incorrect" : "Password required";
        HeaderTextBlock.Foreground = isError ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.White;
        DescriptionTextBlock.Text = description;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = PasswordContext.FromNullable(PasswordInput.Password);
        DialogResult = true;
        Close();
    }
}
