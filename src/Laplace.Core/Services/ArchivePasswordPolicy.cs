using Laplace.Core.Models;

namespace Laplace.Core.Services;

public static class ArchivePasswordPolicy
{
    public static void EnsureConfirmationMatches(PasswordContext password, PasswordContext confirmation)
    {
        EnsureConfirmationMatches(password.Password, confirmation.Password);
    }

    public static void EnsureConfirmationMatches(string password, string confirmation)
    {
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            throw new ArgumentException("Passwords do not match.");
        }
    }
}
