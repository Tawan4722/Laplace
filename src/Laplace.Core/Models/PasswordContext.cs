namespace Laplace.Core.Models;

public sealed class PasswordContext
{
    public PasswordContext(string? password, byte[]? keyfileHash = null)
    {
        var normalizedPassword = string.IsNullOrEmpty(password) ? null : password;
        var normalizedKeyfileHash = keyfileHash is { Length: > 0 } ? keyfileHash.ToArray() : null;
        if (normalizedPassword is null && normalizedKeyfileHash is null)
        {
            throw new ArgumentException("Password context must include a password, a keyfile, or both.", nameof(password));
        }

        Password = normalizedPassword;
        KeyfileHash = normalizedKeyfileHash;
    }

    public string? Password { get; }
    public byte[]? KeyfileHash { get; }
    public bool HasPassword => !string.IsNullOrEmpty(Password);
    public bool HasKeyfile => KeyfileHash is { Length: > 0 };

    public static PasswordContext? FromNullable(string? password)
    {
        return string.IsNullOrEmpty(password) ? null : new PasswordContext(password);
    }
}
