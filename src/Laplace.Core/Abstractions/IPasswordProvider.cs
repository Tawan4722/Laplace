using Laplace.Core.Models;

namespace Laplace.Core.Abstractions;

public interface IPasswordProvider
{
    ValueTask<PasswordContext?> GetPasswordAsync(PasswordRequest request, CancellationToken cancellationToken = default);
}
