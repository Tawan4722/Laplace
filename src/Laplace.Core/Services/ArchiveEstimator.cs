using Laplace.Core.Abstractions;
using Laplace.Core.Compression;
using Laplace.Core.Enums;
using Laplace.Core.Models;

namespace Laplace.Core.Services;

public sealed class ArchiveEstimator
{
    private const int SampleSizeBytes = 64 * 1024;
    private const long MetadataBaseBytes = 256;
    private const long EstimatedFileEntryBytes = 96;
    private const long EstimatedBlockEntryBytes = 80;

    private readonly ICompressorRegistry _compressorRegistry;
    private readonly AdaptiveCompressionEngine _adaptiveCompressionEngine;

    public ArchiveEstimator(ICompressorRegistry compressorRegistry, AdaptiveCompressionEngine? adaptiveCompressionEngine = null)
    {
        _compressorRegistry = compressorRegistry;
        _adaptiveCompressionEngine = adaptiveCompressionEngine ?? new AdaptiveCompressionEngine();
    }

    public async Task<ArchiveEstimate> EstimateAsync(
        IEnumerable<string> inputPaths,
        CreateArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scanned = ArchivePathScanner.Scan(inputPaths);
        if (scanned.Count == 0)
        {
            throw new InvalidOperationException("No input files or folders were found.");
        }

        var files = scanned.Where(x => !x.IsDirectory).ToList();
        var folders = scanned.Count - files.Count;
        var totalBytes = files.Sum(x => new FileInfo(x.FullPath).Length);
        long processedBytes = 0;
        long estimatedPayloadBytes = 0;
        var sampleCount = 0;
        var methods = new HashSet<CompressionMethod>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new FileInfo(file.FullPath);
            var estimate = await EstimateFileAsync(file, info.Length, options, cancellationToken).ConfigureAwait(false);
            estimatedPayloadBytes += estimate.EstimatedCompressedSize;
            sampleCount += estimate.SampleCount;
            foreach (var method in estimate.Methods)
            {
                methods.Add(method);
            }

            processedBytes += info.Length;
            progress?.Report(new ArchiveOperationProgress
            {
                CurrentItem = file.RelativePath,
                ProcessedBytes = processedBytes,
                TotalBytes = totalBytes,
                Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
            });
        }

        var estimatedBlocks = files.Sum(x => Math.Max(1L, (new FileInfo(x.FullPath).Length + options.BlockSizeBytes - 1) / options.BlockSizeBytes));
        var metadataBytes = MetadataBaseBytes
            + scanned.Sum(x => EstimatedFileEntryBytes + x.RelativePath.Length * 2L)
            + estimatedBlocks * EstimatedBlockEntryBytes
            + (options.Password is null ? 0 : estimatedBlocks * (ArchiveEncryption.NonceSizeBytes + ArchiveEncryption.TagSizeBytes));
        var estimatedCompressedSize = Math.Max(0, estimatedPayloadBytes + metadataBytes);

        return new ArchiveEstimate
        {
            OriginalSize = totalBytes,
            EstimatedCompressedSize = estimatedCompressedSize,
            FileCount = files.Count,
            FolderCount = folders,
            SampleCount = sampleCount,
            Confidence = GetConfidence(files.Count, totalBytes, sampleCount),
            LikelyMethods = methods.Count == 0
                ? [CompressionMethod.Raw.ToString()]
                : methods.OrderBy(x => (int)x).Select(x => x.ToString()).ToArray(),
            Notes = "Estimate is based on sampled trial compression and includes approximate LPC metadata overhead."
        };
    }

    private async Task<FileEstimate> EstimateFileAsync(
        InputEntry file,
        long fileSize,
        CreateArchiveOptions options,
        CancellationToken cancellationToken)
    {
        if (fileSize == 0)
        {
            return new FileEstimate(0, 0, new HashSet<CompressionMethod> { CompressionMethod.Raw });
        }

        var samples = await ReadSamplesAsync(file.FullPath, fileSize, cancellationToken).ConfigureAwait(false);
        if (samples.Count == 0)
        {
            return new FileEstimate(fileSize, 0, new HashSet<CompressionMethod> { CompressionMethod.Raw });
        }

        long sampledBytes = 0;
        double weightedRatio = 0;
        var methods = new HashSet<CompressionMethod>();
        foreach (var sample in samples)
        {
            var result = EstimateSample(file.RelativePath, sample, options.Mode);
            sampledBytes += sample.Length;
            weightedRatio += result.Ratio * sample.Length;
            methods.Add(result.Method);
        }

        var ratio = sampledBytes == 0 ? 1d : weightedRatio / sampledBytes;
        var estimatedBytes = Math.Clamp((long)Math.Ceiling(fileSize * ratio), 0, fileSize);
        return new FileEstimate(estimatedBytes, samples.Count, methods);
    }

    private SampleEstimate EstimateSample(string relativePath, byte[] sample, CompressionMode mode)
    {
        if (sample.Length == 0)
        {
            return new SampleEstimate(CompressionMethod.Raw, 1d);
        }

        var analysis = _adaptiveCompressionEngine.Analyze(relativePath, sample);
        var bestMethod = CompressionMethod.Raw;
        var bestRatio = 1d;
        var bestScore = double.MinValue;

        foreach (var candidate in _adaptiveCompressionEngine.GetCandidates(mode, analysis).Distinct())
        {
            if (candidate == CompressionMethod.Raw)
            {
                continue;
            }

            try
            {
                var compressed = _compressorRegistry.GetCompressor(candidate).Compress(sample);
                var ratio = (double)compressed.Length / sample.Length;
                if (ratio >= 1.0)
                {
                    continue;
                }

                var score = _adaptiveCompressionEngine.Score(
                    mode,
                    candidate,
                    analysis,
                    ratioAfterCompression: ratio,
                    estimatedRelativeSpeed: EstimateRelativeSpeed(candidate),
                    estimatedRelativeMemoryUse: EstimateRelativeMemoryUse(candidate));

                if (analysis.LikelyAlreadyCompressed && ratio > 0.98)
                {
                    score -= 0.20;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRatio = ratio;
                    bestMethod = candidate;
                }
            }
            catch
            {
                // Skip unavailable or unsuitable codecs during estimation.
            }
        }

        return new SampleEstimate(bestMethod, bestRatio);
    }

    private static async Task<IReadOnlyList<byte[]>> ReadSamplesAsync(
        string path,
        long fileSize,
        CancellationToken cancellationToken)
    {
        var sampleLength = (int)Math.Min(SampleSizeBytes, fileSize);
        var offsets = GetSampleOffsets(fileSize, sampleLength).ToArray();
        var samples = new List<byte[]>(offsets.Length);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            SampleSizeBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        foreach (var offset in offsets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            stream.Position = offset;
            var buffer = new byte[(int)Math.Min(sampleLength, fileSize - offset)];
            var read = 0;
            while (read < buffer.Length)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                read += bytesRead;
            }

            if (read == buffer.Length)
            {
                samples.Add(buffer);
            }
            else if (read > 0)
            {
                samples.Add(buffer[..read]);
            }
        }

        return samples;
    }

    private static IEnumerable<long> GetSampleOffsets(long fileSize, int sampleLength)
    {
        if (fileSize <= sampleLength)
        {
            yield return 0;
            yield break;
        }

        var offsets = new SortedSet<long>
        {
            0,
            Math.Max(0, fileSize / 2 - sampleLength / 2),
            Math.Max(0, fileSize - sampleLength)
        };

        foreach (var offset in offsets)
        {
            yield return offset;
        }
    }

    private static string GetConfidence(int fileCount, long totalBytes, int sampleCount)
    {
        if (totalBytes == 0)
        {
            return "high";
        }

        if (sampleCount >= Math.Min(fileCount * 3, 9) && totalBytes >= SampleSizeBytes)
        {
            return "medium";
        }

        return fileCount <= 3 ? "high" : "medium";
    }

    private static double EstimateRelativeSpeed(CompressionMethod method)
    {
        return method switch
        {
            CompressionMethod.Raw => 1.00,
            CompressionMethod.Lz4Fast => 0.95,
            CompressionMethod.ZstdFast => 0.80,
            CompressionMethod.ZstdBalanced => 0.62,
            CompressionMethod.ZstdHigh => 0.42,
            CompressionMethod.LzmaMax => 0.25,
            CompressionMethod.DeflateFallback => 0.55,
            _ => 0.50
        };
    }

    private static double EstimateRelativeMemoryUse(CompressionMethod method)
    {
        return method switch
        {
            CompressionMethod.Raw => 0.05,
            CompressionMethod.Lz4Fast => 0.15,
            CompressionMethod.ZstdFast => 0.28,
            CompressionMethod.ZstdBalanced => 0.40,
            CompressionMethod.ZstdHigh => 0.60,
            CompressionMethod.LzmaMax => 0.72,
            CompressionMethod.DeflateFallback => 0.33,
            _ => 0.40
        };
    }

    private sealed record SampleEstimate(CompressionMethod Method, double Ratio);
    private sealed record FileEstimate(long EstimatedCompressedSize, int SampleCount, IReadOnlySet<CompressionMethod> Methods);
}
