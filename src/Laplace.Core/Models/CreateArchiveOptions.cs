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
    public bool BlockSizeExplicitlySet { get; set; }
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
    public long? AvailableCompressionMemoryBytes { get; set; }
    internal int? LzmaDictionarySizeBytes { get; set; }
    internal int LzmaFastBytes { get; set; } = 128;
    internal int ZstdLevel { get; set; } = 15;
    internal int? ZstdWindowLog { get; set; }
    internal bool ZstdLongDistanceMatching { get; set; }
    internal bool ZstdForceLongDistanceTrial { get; set; }
    internal long? TotalInputSizeBytes { get; set; }
    public IReadOnlyList<string>? IncludePatterns { get; set; }
    public IReadOnlyList<string>? ExcludePatterns { get; set; }

    public bool Deduplicate { get; set; }
    public bool UseCdc { get; set; }
    public int MinChunkSize { get; set; } = 8192;
    public int AvgChunkSize { get; set; } = 32768;
    public int MaxChunkSize { get; set; } = 262144;
}
