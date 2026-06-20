using Laplace.Core.Models;

namespace Laplace.Core.Compression;

internal sealed record ExtremeCompressionSettings(
    long MemoryBudgetBytes,
    int BlockSizeBytes,
    int DictionarySizeBytes,
    int ZstdLevel,
    int ZstdWindowLog,
    bool ForceLongDistanceTrial,
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
            return new ExtremeCompressionSettings(OneGiB, 256 * 1024 * 1024, 256 * 1024 * 1024, 22, 31, true, 1);
        }

        if (availableMemory >= FiveHundredTwelveMiB)
        {
            return new ExtremeCompressionSettings(FiveHundredTwelveMiB, 128 * 1024 * 1024, 128 * 1024 * 1024, 19, 27, true, 1);
        }

        if (availableMemory >= TwoHundredFiftySixMiB)
        {
            return new ExtremeCompressionSettings(TwoHundredFiftySixMiB, 32 * 1024 * 1024, 32 * 1024 * 1024, 9, 25, false, 1);
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
        options.ZstdLevel = settings.ZstdLevel;
        options.ZstdWindowLog = settings.ZstdWindowLog;
        options.ZstdLongDistanceMatching = true;
        options.ZstdForceLongDistanceTrial = settings.ForceLongDistanceTrial;
        return settings;
    }
}
