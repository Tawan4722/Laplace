using Laplace.Core.Models;

namespace Laplace.Core.Compression;

internal sealed record ExtremeCompressionSettings(
    long MemoryBudgetBytes,
    int BlockSizeBytes,
    int DictionarySizeBytes,
    int Threads);

internal static class ExtremeCompressionPolicy
{
    private const long OneGiB = 1024L * 1024 * 1024;
    private const long FiveHundredTwelveMiB = 512L * 1024 * 1024;
    private const long TwoHundredFiftySixMiB = 256L * 1024 * 1024;

    public static ExtremeCompressionSettings Resolve(CreateArchiveOptions options)
    {
        if (options.BlockSizeExplicitlySet)
        {
            throw new InvalidOperationException("Extreme mode chooses block size automatically; remove --block-size.");
        }

        var availableMemory = options.AvailableCompressionMemoryBytes
            ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        if (availableMemory >= OneGiB)
        {
            return new ExtremeCompressionSettings(OneGiB, 256 * 1024 * 1024, 128 * 1024 * 1024, 1);
        }

        if (availableMemory >= FiveHundredTwelveMiB)
        {
            return new ExtremeCompressionSettings(FiveHundredTwelveMiB, 128 * 1024 * 1024, 64 * 1024 * 1024, 1);
        }

        if (availableMemory >= TwoHundredFiftySixMiB)
        {
            return new ExtremeCompressionSettings(TwoHundredFiftySixMiB, 64 * 1024 * 1024, 32 * 1024 * 1024, 1);
        }

        throw new InvalidOperationException("Extreme mode requires at least 256 MiB of available compression memory.");
    }

    public static ExtremeCompressionSettings Apply(CreateArchiveOptions options)
    {
        var settings = Resolve(options);
        options.BlockSizeBytes = settings.BlockSizeBytes;
        options.Threads = settings.Threads;
        options.LzmaDictionarySizeBytes = settings.DictionarySizeBytes;
        options.LzmaFastBytes = 273;
        return settings;
    }
}
