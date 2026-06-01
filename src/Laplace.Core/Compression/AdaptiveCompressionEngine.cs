using Laplace.Core.Enums;

namespace Laplace.Core.Compression;

public sealed class AdaptiveCompressionEngine
{
    public CompressionAnalysis Analyze(string path, ReadOnlySpan<byte> sampledBytes)
    {
        var extension = Path.GetExtension(path);
        var category = FileTypeDetector.Detect(path, sampledBytes);
        var entropy = EstimateEntropy(sampledBytes);
        var repetition = EstimateRepetition(sampledBytes);
        var patternReuse = EstimatePatternReuse(sampledBytes);
        var textRatio = EstimateTextRatio(sampledBytes);
        var zeroRatio = EstimateZeroRatio(sampledBytes);
        var compressibility = Math.Clamp(
            (8d - entropy) / 8d
            + repetition * 0.25
            + patternReuse * 0.45
            + zeroRatio * 0.30
            + (textRatio >= 0.90 ? 0.18 : 0d),
            0d,
            1d);
        var alreadyCompressed = IsKnownCompressedExtension(extension)
            || (entropy >= 7.85 && patternReuse < 0.03 && repetition < 0.01);

        return new CompressionAnalysis
        {
            FileTypeCategory = category,
            Entropy = entropy,
            RepetitionRatio = repetition,
            PatternReuseRatio = patternReuse,
            TextRatio = textRatio,
            ZeroRatio = zeroRatio,
            CompressibilityEstimate = compressibility,
            LikelyAlreadyCompressed = alreadyCompressed
        };
    }

    public IReadOnlyList<CompressionMethod> GetCandidates(CompressionMode mode, CompressionAnalysis analysis)
    {
        if (analysis.LikelyAlreadyCompressed && mode != CompressionMode.Intensive)
        {
            return [CompressionMethod.Raw, CompressionMethod.Lz4Fast, CompressionMethod.ZstdFast];
        }

        return mode switch
        {
            CompressionMode.Fast => [CompressionMethod.Lz4Fast, CompressionMethod.ZstdFast, CompressionMethod.DeflateFallback, CompressionMethod.Raw],
            CompressionMode.Balanced => [CompressionMethod.ZstdBalanced, CompressionMethod.ZstdFast, CompressionMethod.DeflateFallback, CompressionMethod.Lz4Fast, CompressionMethod.Raw],
            CompressionMode.Maximum => [CompressionMethod.LzmaMax, CompressionMethod.ZstdHigh, CompressionMethod.ZstdBalanced, CompressionMethod.DeflateFallback, CompressionMethod.ZstdFast, CompressionMethod.Raw],
            CompressionMode.Intensive => [CompressionMethod.LzmaMax, CompressionMethod.ZstdHigh, CompressionMethod.ZstdBalanced, CompressionMethod.DeflateFallback, CompressionMethod.ZstdFast, CompressionMethod.Lz4Fast, CompressionMethod.Raw],
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
            CompressionMode.Intensive => (0.85, 0.05, 0.05, 0.05),
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

    public static double EstimatePatternReuse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            return 0d;
        }

        Span<int> buckets = stackalloc int[4096];
        var windows = 0;
        var repeated = 0;
        for (var i = 0; i <= bytes.Length - 4; i += 4)
        {
            var hash = unchecked((bytes[i] * 16777619) ^ (bytes[i + 1] * 65537) ^ (bytes[i + 2] * 257) ^ bytes[i + 3]);
            var bucket = hash & 0x0FFF;
            if (buckets[bucket] > 0)
            {
                repeated++;
            }

            buckets[bucket]++;
            windows++;
        }

        return windows == 0 ? 0d : (double)repeated / windows;
    }

    public static double EstimateTextRatio(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0d;
        }

        var textLike = 0;
        foreach (var b in bytes)
        {
            if (b is 9 or 10 or 13 || (b >= 32 && b <= 126) || b >= 128)
            {
                textLike++;
            }
        }

        return (double)textLike / bytes.Length;
    }

    public static double EstimateZeroRatio(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0d;
        }

        var zeros = 0;
        foreach (var b in bytes)
        {
            if (b == 0)
            {
                zeros++;
            }
        }

        return (double)zeros / bytes.Length;
    }

    private static IReadOnlyList<CompressionMethod> GetAutoCandidates(CompressionAnalysis analysis)
    {
        if (analysis.LikelyAlreadyCompressed)
        {
            return [CompressionMethod.Raw, CompressionMethod.Lz4Fast, CompressionMethod.ZstdFast];
        }

        if (analysis.CompressibilityEstimate > 0.72)
        {
            return [CompressionMethod.LzmaMax, CompressionMethod.ZstdHigh, CompressionMethod.ZstdBalanced, CompressionMethod.DeflateFallback, CompressionMethod.ZstdFast, CompressionMethod.Raw];
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
            (CompressionMethod.LzmaMax, FileTypeCategory.Database or FileTypeCategory.TextLike or FileTypeCategory.SourceCode or FileTypeCategory.Log) => 0.92,
            _ => 0.55
        };
    }

    private static bool IsKnownCompressedExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".zip" or ".rar" or ".7z" or ".gz" or ".xz" or ".zst" or ".bz2" or ".cab" or ".lpc" => true,
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".heic" => true,
            ".mp4" or ".mkv" or ".mov" or ".webm" or ".wmv" or ".m4v" => true,
            ".mp3" or ".aac" or ".flac" or ".ogg" or ".m4a" or ".opus" => true,
            ".pdf" or ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" => true,
            _ => false
        };
    }
}

public sealed class CompressionAnalysis
{
    public FileTypeCategory FileTypeCategory { get; init; }
    public double Entropy { get; init; }
    public double RepetitionRatio { get; init; }
    public double PatternReuseRatio { get; init; }
    public double TextRatio { get; init; }
    public double ZeroRatio { get; init; }
    public double CompressibilityEstimate { get; init; }
    public bool LikelyAlreadyCompressed { get; init; }
}
