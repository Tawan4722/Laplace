namespace Laplace.Core.Models;

public sealed class ArchiveOperationProgress
{
    public required string CurrentItem { get; init; }
    public required long ProcessedBytes { get; init; }
    public required long TotalBytes { get; init; }
    public required double Percent { get; init; }
}
