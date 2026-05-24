using Laplace.Core.Enums;

namespace Laplace.Core.Models;

public sealed class BlockEntryRecord
{
    public long BlockId { get; set; }
    public long OwningFileEntryId { get; set; }
    public int OriginalBlockSize { get; set; }
    public int CompressedBlockSize { get; set; }
    public CompressionMethod CompressionMethod { get; set; }
    public int CompressionLevel { get; set; }
    public long DataOffset { get; set; }
    public uint BlockChecksumCrc32C { get; set; }
    public uint Flags { get; set; }
    public bool IsRaw { get; set; }
    public byte[] EncryptionNonce { get; set; } = [];
    public byte[] EncryptionTag { get; set; } = [];
}
