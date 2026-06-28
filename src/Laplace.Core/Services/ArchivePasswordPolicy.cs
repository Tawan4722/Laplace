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
        var passSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(password.AsSpan());
        var confSpan = System.Runtime.InteropServices.MemoryMarshal.AsBytes(confirmation.AsSpan());
        if (!CryptographicOperations.FixedTimeEquals(passSpan, confSpan))
        {
            throw new ArgumentException("Passwords do not match.");
        }
    }
}
