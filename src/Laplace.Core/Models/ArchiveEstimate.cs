namespace Laplace.Core.Models;

public sealed class ArchiveEstimate
{
    public long OriginalSize { get; init; }
    public long EstimatedCompressedSize { get; init; }
    public double EstimatedRatio => OriginalSize == 0 ? 1d : (double)EstimatedCompressedSize / OriginalSize;
    public double EstimatedReduction => 1d - EstimatedRatio;
    public int FileCount { get; init; }
    public int FolderCount { get; init; }
    public int SampleCount { get; init; }
    public string Confidence { get; init; } = "low";
    public string[] LikelyMethods { get; init; } = [];
    public string Notes { get; init; } = string.Empty;
}
