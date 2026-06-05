using Laplace.Core.Enums;

namespace Laplace.Core.Models;

public sealed class CreateArchiveOptions
{
    public const int MinimumKeyDerivationIterations = 210_000;
    public const int DefaultKeyDerivationIterations = 600_000;
    public const int MaximumKeyDerivationIterations = 5_000_000;
    public const int MinimumArgon2Iterations = 1;
    public const int DefaultArgon2Iterations = 3;
    public const int MaximumArgon2Iterations = 10;
    public const int MinimumArgon2MemoryKiB = 16 * 1024;
    public const int DefaultArgon2MemoryKiB = 64 * 1024;
    public const int MaximumArgon2MemoryKiB = 1024 * 1024;
    public const int MaximumArgon2Parallelism = 64;

    public CompressionMode Mode { get; set; } = CompressionMode.Balanced;
    public int BlockSizeBytes { get; set; } = 8 * 1024 * 1024;
    public SolidMode SolidMode { get; set; } = SolidMode.Auto;
    public bool VerifyAfterCompression { get; set; } = true;
    public int Threads { get; set; } = Environment.ProcessorCount;
    public string Comment { get; set; } = string.Empty;
    public PasswordContext? Password { get; set; }
    public KeyDerivationAlgorithm KeyDerivationAlgorithm { get; set; } = KeyDerivationAlgorithm.Argon2id;
    public int KeyDerivationIterations { get; set; } = DefaultKeyDerivationIterations;
    public int Argon2Iterations { get; set; } = DefaultArgon2Iterations;
    public int Argon2MemoryKiB { get; set; } = DefaultArgon2MemoryKiB;
    public int Argon2Parallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 1, 4);
    public bool LockArchive { get; set; }
    public bool EncryptMetadata { get; set; }
    public long? VolumeSizeBytes { get; set; }
    public int RecoveryPercent { get; set; }
}
