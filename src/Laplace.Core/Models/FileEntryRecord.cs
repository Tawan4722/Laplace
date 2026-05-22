using Laplace.Core.Enums;

namespace Laplace.Core.Models;

public sealed class FileEntryRecord
{
    public long EntryId { get; set; }
    public long ParentFolderId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long OriginalSize { get; set; }
    public long CompressedSize { get; set; }
    public long CreatedUnixMilliseconds { get; set; }
    public long ModifiedUnixMilliseconds { get; set; }
    public int FileAttributes { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsSymlink { get; set; }
    public long FirstBlockIndex { get; set; } = -1;
    public int BlockCount { get; set; }
    public string CompressionSummary { get; set; } = string.Empty;
    public ChecksumType ChecksumType { get; set; } = ChecksumType.Sha256;
    public byte[] FileChecksum { get; set; } = [];
    public string OptionalMetadataJson { get; set; } = string.Empty;
}
