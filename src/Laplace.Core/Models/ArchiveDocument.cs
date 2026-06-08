namespace Laplace.Core.Models;

public sealed class ArchiveDocument
{
    public required ArchiveHeader Header { get; init; }
    public required IReadOnlyList<FileEntryRecord> FileEntries { get; init; }
    public required IReadOnlyList<BlockEntryRecord> BlockEntries { get; init; }
}
