using Laplace.Core.Abstractions;
using Laplace.Core.Models;
using System.Text;
using System.Windows.Forms;

namespace Laplace.Cli;

internal sealed class ConsolePasswordProvider : IPasswordProvider
{
    public ValueTask<PasswordContext?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken = default)
    {
        if (Console.IsInputRedirected)
        {
            return ValueTask.FromResult<PasswordContext?>(null);
        }

        Console.Error.Write($"{request.Operation} password for {Path.GetFileName(request.ArchivePath)}: ");
        var password = ReadMaskedLine();
        Console.Error.WriteLine();
        return ValueTask.FromResult(PasswordContext.FromNullable(password));
    }

    private static string ReadMaskedLine()
    {
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                    Console.Error.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
                Console.Error.Write("*");
            }
        }

        return value.ToString();
    }
}

internal sealed class WindowsPopupPasswordProvider : IPasswordProvider
{
    public ValueTask<PasswordContext?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            return ValueTask.FromResult<PasswordContext?>(null);
        }

        try
        {
            Application.EnableVisualStyles();
            using var form = new Form
            {
                Text = "Laplace Password",
                Width = 420,
                Height = 180,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MinimizeBox = false,
                MaximizeBox = false
            };

            var fileLabel = new Label
            {
                Left = 16,
                Top = 16,
                Width = 370,
                Height = 24,
                Text = Path.GetFileName(request.ArchivePath)
            };
            var promptLabel = new Label
            {
                Left = 16,
                Top = 48,
                Width = 370,
                Height = 20,
                Text = $"{request.Operation} password"
            };
            var passwordBox = new TextBox
            {
                Left = 16,
                Top = 72,
                Width = 370,
                UseSystemPasswordChar = true
            };
            var okButton = new Button
            {
                Text = "OK",
                Left = 230,
                Width = 75,
                Top = 108,
                DialogResult = DialogResult.OK
            };
            var cancelButton = new Button
            {
                Text = "Cancel",
                Left = 310,
                Width = 75,
                Top = 108,
                DialogResult = DialogResult.Cancel
            };

            form.Controls.AddRange([fileLabel, promptLabel, passwordBox, okButton, cancelButton]);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            var result = form.ShowDialog();
            return ValueTask.FromResult(result == DialogResult.OK
                ? PasswordContext.FromNullable(passwordBox.Text)
                : null);
        }
        catch
        {
            return ValueTask.FromResult<PasswordContext?>(null);
        }
    }
}

internal sealed class FallbackPasswordProvider : IPasswordProvider
{
    private readonly IReadOnlyList<IPasswordProvider> _providers;

    public FallbackPasswordProvider(params IPasswordProvider[] providers)
    {
        _providers = providers;
    }

    public async ValueTask<PasswordContext?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            var password = await provider.GetPasswordAsync(request, cancellationToken).ConfigureAwait(false);
            if (password is not null)
            {
                return password;
            }
        }

        return null;
    }
}
