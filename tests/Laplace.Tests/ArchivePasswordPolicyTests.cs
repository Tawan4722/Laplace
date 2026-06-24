using Laplace.Core.Models;
using Laplace.Core.Services;
using Xunit;

namespace Laplace.Tests;

public sealed class ArchivePasswordPolicyTests
{
    [Fact]
    public void EnsureConfirmationMatches_RejectsMismatch()
    {
        var password = new PasswordContext("correct horse battery staple");
        var confirmation = new PasswordContext("wrong password");

        Assert.Throws<ArgumentException>(() => ArchivePasswordPolicy.EnsureConfirmationMatches(password, confirmation));
    }
}
