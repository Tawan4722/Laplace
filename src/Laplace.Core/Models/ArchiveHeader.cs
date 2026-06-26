namespace Laplace.Core.Models;

public sealed class ArchiveHeader
{
    public const string Magic = "LPC1";
    public const ushort EncryptionFlag = 1;
    public const ushort LockedFlag = 2;
    public const ushort SolidFlag = 4;
    public const ushort MetadataEncryptionFlag = 8;
    public const ushort RecoveryRecordFlag = 16;
    public const byte EncryptionAlgorithmAes256Gcm = 1;

    public ushort FormatVersion { get; set; } = 1;
    public ushort ArchiveFlags { get; set; }
    public long CreatedUnixMilliseconds { get; set; }
    public uint CreatorVersion { get; set; } = 0x00010000;
    public uint DefaultBlockSize { get; set; }
    public long FileEntryCount { get; set; }
    public long BlockEntryCount { get; set; }
    public long FileTableOffset { get; set; }
    public long BlockTableOffset { get; set; }
    public long DataSectionOffset { get; set; }
    public string Comment { get; set; } = string.Empty;
    public uint HeaderChecksumCrc32C { get; set; }
    public byte EncryptionAlgorithmId { get; set; }
    public byte KeyDerivationAlgorithmId { get; set; }
    public int KeyDerivationIterations { get; set; }
    public int KeyDerivationMemoryKiB { get; set; }
    public int KeyDerivationParallelism { get; set; }
    public byte[] EncryptionSalt { get; set; } = [];
    public long RecoveryRecordOffset { get; set; }
    public long RecoveryRecordLength { get; set; }
    public int RecoveryPercent { get; set; }
    public string OptionalHeaderMetadataJson { get; set; } = string.Empty;


    public bool IsEncrypted => (ArchiveFlags & EncryptionFlag) != 0;
    public bool IsLocked => (ArchiveFlags & LockedFlag) != 0;
    public bool IsSolid => (ArchiveFlags & SolidFlag) != 0;
    public bool IsMetadataEncrypted => (ArchiveFlags & MetadataEncryptionFlag) != 0;
    public bool HasRecoveryRecord => (ArchiveFlags & RecoveryRecordFlag) != 0;
}
