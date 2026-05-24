using Laplace.Core.Enums;

namespace Laplace.Core.Models;

public sealed class CreateArchiveOptions
{
    public const int DefaultKeyDerivationIterations = 210_000;

    public CompressionMode Mode { get; set; } = CompressionMode.Balanced;
    public int BlockSizeBytes { get; set; } = 8 * 1024 * 1024;
    public SolidMode SolidMode { get; set; } = SolidMode.Auto;
    public bool VerifyAfterCompression { get; set; } = true;
    public int Threads { get; set; } = Environment.ProcessorCount;
    public string Comment { get; set; } = string.Empty;
    public PasswordContext? Password { get; set; }
    public int KeyDerivationIterations { get; set; } = DefaultKeyDerivationIterations;
}
