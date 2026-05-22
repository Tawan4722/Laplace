using Laplace.Core.Enums;

namespace Laplace.Core.Compression;

public sealed class AdaptiveCompressionEngine
{
    public CompressionAnalysis Analyze(string path, ReadOnlySpan<byte> sampledBytes)
    {
        var category = FileTypeDetector.Detect(path, sampledBytes);
        var entropy = EstimateEntropy(sampledBytes);
        var repetition = EstimateRepetition(sampledBytes);
        var compressibility = Math.Clamp((8d - entropy) / 8d + repetition * 0.4, 0d, 1d);
        var alreadyCompressed = category is FileTypeCategory.Image or FileTypeCategory.Video or FileTypeCategory.Audio or FileTypeCategory.Archive
            || entropy >= 7.65;

        return new CompressionAnalysis
        {
            FileTypeCategory = category,
            Entropy = entropy,
            RepetitionRatio = repetition,
            CompressibilityEstimate = compressibility,
            LikelyAlreadyCompressed = alreadyCompressed
        };
    }

    public IReadOnlyList<CompressionMethod> GetCandidates(CompressionMode mode, CompressionAnalysis analysis)
    {
        if (analysis.LikelyAlreadyCompressed)
        {
            return [CompressionMethod.Raw, CompressionMethod.Lz4Fast, CompressionMethod.ZstdFast];
        }

        return mode switch
        {
            CompressionMode.Fast => [CompressionMethod.Lz4Fast, CompressionMethod.ZstdFast, CompressionMethod.DeflateFallback, CompressionMethod.Raw],
            CompressionMode.Balanced => [CompressionMethod.ZstdBalanced, CompressionMethod.ZstdFast, CompressionMethod.DeflateFallback, CompressionMethod.Lz4Fast, CompressionMethod.Raw],
            CompressionMode.Maximum => [CompressionMethod.ZstdHigh, CompressionMethod.ZstdBalanced, CompressionMethod.DeflateFallback, CompressionMethod.ZstdFast, CompressionMethod.Raw],
            CompressionMode.Auto => GetAutoCandidates(analysis),
            _ => [CompressionMethod.ZstdBalanced, CompressionMethod.Raw]
        };
    }

    public double Score(
        CompressionMode mode,
        CompressionMethod method,
        CompressionAnalysis analysis,
        double ratioAfterCompression,
        double estimatedRelativeSpeed,
        double estimatedRelativeMemoryUse)
    {
        var (ratioWeight, speedWeight, memoryWeight, fileTypeWeight) = mode switch
        {
            CompressionMode.Fast => (0.25, 0.55, 0.10, 0.10),
            CompressionMode.Balanced => (0.45, 0.35, 0.10, 0.10),
            CompressionMode.Maximum => (0.70, 0.15, 0.10, 0.05),
            _ => (0.50, 0.30, 0.10, 0.10)
        };

        var ratioScore = 1d - ratioAfterCompression;
        var fileTypeScore = GetFileTypeAffinity(method, analysis.FileTypeCategory);
        var memoryScore = 1d - estimatedRelativeMemoryUse;

        return ratioScore * ratioWeight
            + estimatedRelativeSpeed * speedWeight
            + memoryScore * memoryWeight
            + fileTypeScore * fileTypeWeight;
    }

    public static double EstimateEntropy(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0d;
        }

        Span<int> counts = stackalloc int[256];
        foreach (var b in bytes)
        {
            counts[b]++;
        }

        var entropy = 0d;
        var length = (double)bytes.Length;
        for (var i = 0; i < 256; i++)
        {
            var count = counts[i];
            if (count == 0)
            {
                continue;
            }

            var p = count / length;
            entropy -= p * Math.Log(p, 2);
        }

        return entropy;
    }

    public static double EstimateRepetition(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
        {
            return 0;
        }

        var repeatedPairs = 0;
        for (var i = 1; i < bytes.Length; i++)
        {
            if (bytes[i] == bytes[i - 1])
            {
                repeatedPairs++;
            }
        }

        return (double)repeatedPairs / (bytes.Length - 1);
    }

    private static IReadOnlyList<CompressionMethod> GetAutoCandidates(CompressionAnalysis analysis)
    {
        if (analysis.LikelyAlreadyCompressed)
        {
            return [CompressionMethod.Raw, CompressionMethod.Lz4Fast, CompressionMethod.ZstdFast];
        }

        if (analysis.CompressibilityEstimate > 0.72)
        {
            return [CompressionMethod.ZstdHigh, CompressionMethod.ZstdBalanced, CompressionMethod.DeflateFallback, CompressionMethod.ZstdFast, CompressionMethod.Raw];
        }

        if (analysis.CompressibilityEstimate > 0.45)
        {
            return [CompressionMethod.ZstdBalanced, CompressionMethod.ZstdFast, CompressionMethod.DeflateFallback, CompressionMethod.Lz4Fast, CompressionMethod.Raw];
        }

        return [CompressionMethod.ZstdFast, CompressionMethod.Lz4Fast, CompressionMethod.Raw];
    }

    private static double GetFileTypeAffinity(CompressionMethod method, FileTypeCategory category)
    {
        return (method, category) switch
        {
            (CompressionMethod.Raw, FileTypeCategory.Image or FileTypeCategory.Video or FileTypeCategory.Audio or FileTypeCategory.Archive or FileTypeCategory.Executable) => 0.95,
            (CompressionMethod.Lz4Fast, FileTypeCategory.Binary or FileTypeCategory.Executable) => 0.85,
            (CompressionMethod.DeflateFallback, FileTypeCategory.TextLike or FileTypeCategory.SourceCode or FileTypeCategory.Log) => 0.75,
            (CompressionMethod.ZstdFast, FileTypeCategory.Binary or FileTypeCategory.Unknown) => 0.8,
            (CompressionMethod.ZstdBalanced, FileTypeCategory.Document or FileTypeCategory.Database or FileTypeCategory.TextLike or FileTypeCategory.SourceCode) => 0.88,
            (CompressionMethod.ZstdHigh, FileTypeCategory.Document or FileTypeCategory.Database or FileTypeCategory.TextLike or FileTypeCategory.SourceCode) => 0.9,
            _ => 0.55
        };
    }
}

public sealed class CompressionAnalysis
{
    public FileTypeCategory FileTypeCategory { get; init; }
    public double Entropy { get; init; }
    public double RepetitionRatio { get; init; }
    public double CompressibilityEstimate { get; init; }
    public bool LikelyAlreadyCompressed { get; init; }
}
