using Laplace.Core.Models;
using System.Security.Cryptography;
using System.Text;

namespace Laplace.Core.Services;

public static class ArchivePasswordPolicy
{
    public static void EnsureConfirmationMatches(PasswordContext password, PasswordContext confirmation)
    {
        EnsureConfirmationMatches(password.Password ?? string.Empty, confirmation.Password ?? string.Empty);
    }

    public static void EnsureConfirmationMatches(string password, string confirmation)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var confirmationBytes = Encoding.UTF8.GetBytes(confirmation);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(passwordBytes, confirmationBytes))
            {
                throw new ArgumentException("Passwords do not match.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(confirmationBytes);
        }
    }
}
