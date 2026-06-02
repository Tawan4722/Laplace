using Laplace.Core.Enums;

namespace Laplace.Core.Models;

public sealed class MutateArchiveOptions
{
    public CompressionMode Mode { get; set; } = CompressionMode.Balanced;
    public int? BlockSizeBytes { get; set; }
    public PasswordContext? Password { get; set; }
    public bool Overwrite { get; set; } = true;
    public bool VerifyAfterRewrite { get; set; } = true;
}

public sealed class ArchiveFindOptions
{
    public string NamePattern { get; set; } = "*";
    public string? Text { get; set; }
    public PasswordContext? Password { get; set; }
}
