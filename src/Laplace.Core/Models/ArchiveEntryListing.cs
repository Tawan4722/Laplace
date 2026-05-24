namespace Laplace.Core.Models;

public sealed class ArchiveEntryListing
{
    public long Id { get; init; }
    public bool IsDirectory { get; init; }
    public long OriginalSize { get; init; }
    public long CompressedSize { get; init; }
    public string Method { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public bool IsEncrypted { get; init; }
}
