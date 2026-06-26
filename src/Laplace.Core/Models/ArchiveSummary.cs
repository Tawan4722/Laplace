namespace Laplace.Core.Models;

public sealed class ArchiveSummary
{
    public string Format { get; init; } = string.Empty;
    public int ArchiveVersion { get; init; }
    public int FileCount { get; init; }
    public int FolderCount { get; init; }
    public int BlockCount { get; init; }
    public long OriginalSize { get; init; }
    public long CompressedSize { get; init; }
    public double Ratio { get; init; }
    public string[] MethodsUsed { get; init; } = [];
    public DateTime? CreatedUtc { get; init; }
    public bool IsEncrypted { get; init; }
    public bool IsLocked { get; init; }
    public bool IsMetadataEncrypted { get; init; }
    public bool HasRecoveryRecord { get; init; }
    public bool IsVolume { get; init; }
    public string Comment { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public string OptionalHeaderMetadataJson { get; init; } = string.Empty;
}

