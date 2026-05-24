namespace Laplace.Core.Models;

public sealed class PasswordContext
{
    public PasswordContext(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password must not be empty.", nameof(password));
        }

        Password = password;
    }

    public string Password { get; }

    public static PasswordContext? FromNullable(string? password)
    {
        return string.IsNullOrEmpty(password) ? null : new PasswordContext(password);
    }
}
