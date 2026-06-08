namespace Laplace.Core.Models;

public sealed class ArchiveFindResult
{
    public long Id { get; init; }
    public string Path { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long OriginalSize { get; init; }
    public bool NameMatched { get; init; }
    public bool TextMatched { get; init; }
}
