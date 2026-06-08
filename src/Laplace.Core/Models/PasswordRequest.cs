namespace Laplace.Core.Models;

public sealed record PasswordRequest(
    string ArchivePath,
    string Operation,
    bool IsWrite,
    bool IsRetry = false);
