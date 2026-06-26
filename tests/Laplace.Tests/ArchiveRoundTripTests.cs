using Laplace.Compression;
using Laplace.Core.Abstractions;
using Laplace.Core.Compression;
using Laplace.Core.Exceptions;
using Laplace.Core.Enums;
using Laplace.Core.Models;
using Laplace.Core.Security;
using Laplace.Core.Services;
using Laplace.ShellIntegration;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using ZipEntry = ICSharpCode.SharpZipLib.Zip.ZipEntry;
using ZipOutputStream = ICSharpCode.SharpZipLib.Zip.ZipOutputStream;
using Xunit;

namespace Laplace.Tests;

public sealed class ArchiveRoundTripTests
{
    [Fact]
    public async Task Compress_ThenExtract_SingleFile_RoundTrips()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "hello.txt");
        await File.WriteAllTextAsync(sourceFile, string.Join(Environment.NewLine, Enumerable.Repeat("Laplace archive test line", 1000)));
        var archivePath = Path.Combine(root, "sample.lpc");
        var extractPath = Path.Combine(root, "out");

        var registry = new CompressorRegistry();
        var writer = new ArchiveWriter(registry);
        await writer.CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Balanced,
            BlockSizeBytes = 4 * 1024 * 1024,
            VerifyAfterCompression = false
        });

        var extractor = new ArchiveExtractor(registry);
        await extractor.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
        {
            Overwrite = true,
            VerifyChecksums = true
        });

        var extractedFile = Path.Combine(extractPath, "hello.txt");
        Assert.True(File.Exists(extractedFile));
        Assert.Equal(await File.ReadAllTextAsync(sourceFile), await File.ReadAllTextAsync(extractedFile));
    }

    [Fact]
    public async Task Compress_ManyTinyFiles_ListIsReadableWithoutExtraction()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "tiny");
        Directory.CreateDirectory(sourceDir);
        for (var i = 0; i < 200; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(sourceDir, $"f{i:000}.txt"), $"v{i}");
        }

        var archivePath = Path.Combine(root, "tiny.lpc");
        var registry = new CompressorRegistry();
        var writer = new ArchiveWriter(registry);
        await writer.CreateAsync([sourceDir], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Auto,
            BlockSizeBytes = 4 * 1024 * 1024,
            VerifyAfterCompression = false
        });

        var reader = new ArchiveReader();
        var entries = reader.Read(archivePath).FileEntries;
        Assert.Contains(entries, e => e.IsDirectory && e.RelativePath == "tiny");
        Assert.Equal(200, entries.Count(e => !e.IsDirectory));
    }

    [Fact]
    public async Task Compress_MultipleInputPaths_Succeeds()
    {
        var root = CreateTempFolder();
        var inputA = Path.Combine(root, "a.txt");
        var inputBDir = Path.Combine(root, "bdir");
        Directory.CreateDirectory(inputBDir);
        var inputB = Path.Combine(inputBDir, "b.txt");
        await File.WriteAllTextAsync(inputA, "alpha alpha alpha alpha");
        await File.WriteAllTextAsync(inputB, "beta beta beta beta");

        var archivePath = Path.Combine(root, "multi.lpc");
        var extractPath = Path.Combine(root, "out");
        var registry = new CompressorRegistry();
        var writer = new ArchiveWriter(registry);
        await writer.CreateAsync([inputA, inputBDir], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Auto,
            BlockSizeBytes = 4 * 1024 * 1024
        });

        var extractor = new ArchiveExtractor(registry);
        await extractor.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true, VerifyChecksums = true });

        Assert.True(File.Exists(Path.Combine(extractPath, "a.txt")));
        Assert.True(File.Exists(Path.Combine(extractPath, "bdir", "b.txt")));
    }

    [Fact]
    public async Task AlreadyCompressedLikeData_UsesRawFallbackForExpandedBlocks()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "random.bin");
        var data = new byte[512 * 1024];
        Random.Shared.NextBytes(data);
        await File.WriteAllBytesAsync(sourceFile, data);
        var archivePath = Path.Combine(root, "random.lpc");

        var writer = new ArchiveWriter(new CompressorRegistry());
        var archive = await writer.CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Maximum,
            BlockSizeBytes = 256 * 1024
        });

        Assert.Contains(archive.BlockEntries, b => b.IsRaw || b.CompressionMethod == CompressionMethod.Raw);
    }

    [Fact]
    public async Task CompressibleMediaExtension_IsTestedInsteadOfForcedRaw()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "flat-color.bmp");
        var data = new byte[512 * 1024];
        await File.WriteAllBytesAsync(sourceFile, data);
        var archivePath = Path.Combine(root, "bitmap.lpc");

        var writer = new ArchiveWriter(new CompressorRegistry());
        var archive = await writer.CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Auto,
            BlockSizeBytes = 256 * 1024
        });

        Assert.Contains(archive.BlockEntries, b => !b.IsRaw && b.CompressionMethod != CompressionMethod.Raw);
        Assert.True(archive.BlockEntries.Sum(b => b.CompressedBlockSize) < data.Length);
    }

    [Fact]
    public async Task MixedFile_ReevaluatesCompressionForLaterBlocks()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "mixed.bin");
        var random = new byte[128 * 1024];
        Random.Shared.NextBytes(random);
        var repeated = Enumerable.Repeat((byte)'A', 128 * 1024).ToArray();
        await File.WriteAllBytesAsync(sourceFile, random.Concat(repeated).ToArray());
        var archivePath = Path.Combine(root, "mixed.lpc");

        var writer = new ArchiveWriter(new CompressorRegistry());
        var archive = await writer.CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Auto,
            BlockSizeBytes = 128 * 1024
        });

        Assert.Contains(archive.BlockEntries, b => b.IsRaw || b.CompressionMethod == CompressionMethod.Raw);
        Assert.Contains(archive.BlockEntries, b => !b.IsRaw && b.CompressionMethod != CompressionMethod.Raw);
    }

    [Fact]
    public async Task Estimate_CompressibleFile_PredictsSmallerArchive()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "flat-color.bmp");
        await File.WriteAllBytesAsync(sourceFile, new byte[1024 * 1024]);

        var service = new UniversalArchiveService(new CompressorRegistry());
        var estimate = await service.EstimateAsync([sourceFile], new CreateArchiveOptions
        {
            Mode = CompressionMode.Auto,
            VerifyAfterCompression = false
        });

        Assert.Equal(1024 * 1024, estimate.OriginalSize);
        Assert.True(estimate.EstimatedCompressedSize < estimate.OriginalSize / 10);
        Assert.Contains(estimate.LikelyMethods, method => method != CompressionMethod.Raw.ToString());
    }

    [Fact]
    public async Task Estimate_RandomFile_PredictsLittleOrNoReduction()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "random.bin");
        var data = new byte[512 * 1024];
        Random.Shared.NextBytes(data);
        await File.WriteAllBytesAsync(sourceFile, data);

        var service = new UniversalArchiveService(new CompressorRegistry());
        var estimate = await service.EstimateAsync([sourceFile], new CreateArchiveOptions
        {
            Mode = CompressionMode.Auto,
            VerifyAfterCompression = false
        });

        Assert.True(estimate.EstimatedRatio > 0.95);
        Assert.Contains(CompressionMethod.Raw.ToString(), estimate.LikelyMethods);
    }

    [Fact]
    public async Task Estimate_ExtremeMode_AppliesAutomaticResourcePolicy()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "repeated.bin");
        await File.WriteAllBytesAsync(sourceFile, Enumerable.Repeat((byte)'A', 1024 * 1024).ToArray());
        var options = new CreateArchiveOptions
        {
            Mode = CompressionMode.Extreme,
            AvailableCompressionMemoryBytes = 256L * 1024 * 1024
        };

        var estimate = await new UniversalArchiveService(new CompressorRegistry())
            .EstimateAsync([sourceFile], options);

        Assert.Equal(32 * 1024 * 1024, options.BlockSizeBytes);
        Assert.Equal(1, options.Threads);
        Assert.Equal(32 * 1024 * 1024, options.LzmaDictionarySizeBytes);
        Assert.Contains("long-distance matches", estimate.Notes);
        Assert.True(estimate.EstimatedCompressedSize < estimate.OriginalSize);
    }

    [Fact]
    public void IntensiveMode_UsesRatioFocusedCandidates_EvenForAlreadyCompressedInputs()
    {
        var engine = new AdaptiveCompressionEngine();
        var analysis = new CompressionAnalysis
        {
            FileTypeCategory = FileTypeCategory.Archive,
            Entropy = 7.95,
            LikelyAlreadyCompressed = true
        };

        var candidates = engine.GetCandidates(CompressionMode.Intensive, analysis);

        Assert.Equal(CompressionMethod.Zpaq, candidates[0]);
        Assert.Equal(CompressionMethod.Bsc, candidates[1]);
        Assert.Contains(CompressionMethod.Blosc2, candidates);
        Assert.Contains(CompressionMethod.LzmaMax, candidates);
        Assert.Contains(CompressionMethod.ZstdHigh, candidates);
        Assert.Contains(CompressionMethod.Raw, candidates);
        Assert.True(
            engine.Score(CompressionMode.Intensive, CompressionMethod.Zpaq, analysis, 0.20, 0.05, 0.85) >
            engine.Score(CompressionMode.Fast, CompressionMethod.Zpaq, analysis, 0.20, 0.05, 0.85));
    }

    [Fact]
    public void CompressedMode_UsesStrongestCandidates_AndMostRatioFocusedScoring()
    {
        var engine = new AdaptiveCompressionEngine();
        var analysis = new CompressionAnalysis
        {
            FileTypeCategory = FileTypeCategory.SourceCode,
            Entropy = 4.2,
            TextRatio = 0.98,
            CompressibilityEstimate = 0.8
        };

        var candidates = engine.GetCandidates(CompressionMode.Compressed, analysis);

        Assert.Equal(CompressionMethod.Zpaq, candidates[0]);
        Assert.Equal(CompressionMethod.Bsc, candidates[1]);
        Assert.Contains(CompressionMethod.LzmaMax, candidates);
        Assert.True(
            engine.Score(CompressionMode.Compressed, CompressionMethod.LzmaMax, analysis, 0.20, 0.01, 0.90) >
            engine.Score(CompressionMode.Intensive, CompressionMethod.LzmaMax, analysis, 0.20, 0.01, 0.90));
    }

    [Fact]
    public void ExtremeMode_UsesBuiltInRatioOnlyCandidates()
    {
        var engine = new AdaptiveCompressionEngine();
        var analysis = new CompressionAnalysis
        {
            FileTypeCategory = FileTypeCategory.Binary,
            Entropy = 7.9,
            LikelyAlreadyCompressed = true
        };

        Assert.Equal(
            [
                CompressionMethod.LzmaMax,
                CompressionMethod.ZstdHigh,
                CompressionMethod.Blosc2,
                CompressionMethod.DeflateFallback,
                CompressionMethod.Raw
            ],
            engine.GetCandidates(CompressionMode.Extreme, analysis));
        Assert.Equal(
            0.75,
            engine.Score(CompressionMode.Extreme, CompressionMethod.LzmaMax, analysis, 0.25, 0.01, 1.0),
            precision: 6);
    }

    [Fact]
    public void Cmix_OfferedAsCandidate_WhenTotalInputSizeExceedsThresholdAndRatioMode()
    {
        var engine = new AdaptiveCompressionEngine();
        var analysis = new CompressionAnalysis
        {
            FileTypeCategory = FileTypeCategory.Binary,
            Entropy = 7.9,
            LikelyAlreadyCompressed = true
        };

        const long twentyGb = 20L * 1024 * 1024 * 1024;
        
        var candidatesIntensive = engine.GetCandidates(CompressionMode.Intensive, analysis, twentyGb);
        Assert.Equal(CompressionMethod.Cmix, candidatesIntensive[0]);

        var candidatesCompressed = engine.GetCandidates(CompressionMode.Compressed, analysis, twentyGb);
        Assert.Equal(CompressionMethod.Cmix, candidatesCompressed[0]);

        var candidatesExtreme = engine.GetCandidates(CompressionMode.Extreme, analysis, twentyGb);
        Assert.Equal(CompressionMethod.Cmix, candidatesExtreme[0]);
    }

    [Fact]
    public void Cmix_NotOfferedAsCandidate_WhenTotalInputSizeBelowThreshold()
    {
        var engine = new AdaptiveCompressionEngine();
        var analysis = new CompressionAnalysis
        {
            FileTypeCategory = FileTypeCategory.Binary,
            Entropy = 7.9,
            LikelyAlreadyCompressed = true
        };

        const long lessThanTwentyGb = 20L * 1024 * 1024 * 1024 - 1;
        
        var candidates = engine.GetCandidates(CompressionMode.Intensive, analysis, lessThanTwentyGb);
        Assert.DoesNotContain(CompressionMethod.Cmix, candidates);
    }

    [Fact]
    public void Cmix_NotOfferedAsCandidate_WhenModeIsNotRatioMode()
    {
        var engine = new AdaptiveCompressionEngine();
        var analysis = new CompressionAnalysis
        {
            FileTypeCategory = FileTypeCategory.Binary,
            Entropy = 7.9,
            LikelyAlreadyCompressed = true
        };

        const long twentyGb = 20L * 1024 * 1024 * 1024;
        
        var candidates = engine.GetCandidates(CompressionMode.Maximum, analysis, twentyGb);
        Assert.DoesNotContain(CompressionMethod.Cmix, candidates);
    }

    [Theory]
    [InlineData(1024, 256, 256, 22, 31, true)]
    [InlineData(512, 128, 128, 19, 27, true)]
    [InlineData(256, 32, 32, 9, 25, false)]
    public void ExtremePolicy_SelectsMemoryTierAndSingleWorker(
        int availableMiB,
        int blockMiB,
        int dictionaryMiB,
        int zstdLevel,
        int zstdWindowLog,
        bool forceLongDistanceTrial)
    {
        var options = new CreateArchiveOptions
        {
            Mode = CompressionMode.Extreme,
            AvailableCompressionMemoryBytes = availableMiB * 1024L * 1024
        };

        var settings = ExtremeCompressionPolicy.Apply(options);

        Assert.Equal(blockMiB * 1024 * 1024, settings.BlockSizeBytes);
        Assert.Equal(dictionaryMiB * 1024 * 1024, settings.DictionarySizeBytes);
        Assert.Equal(zstdLevel, settings.ZstdLevel);
        Assert.Equal(zstdWindowLog, settings.ZstdWindowLog);
        Assert.Equal(forceLongDistanceTrial, settings.ForceLongDistanceTrial);
        Assert.Equal(1, settings.Threads);
        Assert.Equal(1, options.Threads);
        Assert.Equal(273, options.LzmaFastBytes);
        Assert.Equal(zstdLevel, options.ZstdLevel);
        Assert.Equal(zstdWindowLog, options.ZstdWindowLog);
        Assert.True(options.ZstdLongDistanceMatching);
        Assert.Equal(forceLongDistanceTrial, options.ZstdForceLongDistanceTrial);
    }

    [Fact]
    public void ExtremePolicy_RejectsInsufficientMemoryAndExplicitBlockSize()
    {
        var lowMemory = new CreateArchiveOptions
        {
            Mode = CompressionMode.Extreme,
            AvailableCompressionMemoryBytes = 255L * 1024 * 1024
        };
        var explicitBlock = new CreateArchiveOptions
        {
            Mode = CompressionMode.Extreme,
            BlockSizeExplicitlySet = true,
            AvailableCompressionMemoryBytes = 1024L * 1024 * 1024
        };

        Assert.Throws<InvalidOperationException>(() => ExtremeCompressionPolicy.Resolve(lowMemory));
        Assert.Throws<InvalidOperationException>(() => ExtremeCompressionPolicy.Resolve(explicitBlock));
    }

    [Fact]
    public void ConfigurableLzma_StoresSelectedDictionaryInCoderProperties()
    {
        const int dictionarySize = 32 * 1024 * 1024;
        var compressed = new Laplace.Compression.Compressors.LzmaCompressor(dictionarySize, 273)
            .Compress(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("extreme lzma ", 1000))));

        Assert.True(compressed.Length > 5);
        Assert.Equal(dictionarySize, BitConverter.ToInt32(compressed, 1));
    }

    [Fact]
    public void ConfigurableZstd_LongDistanceMode_RoundTripsDistantReuse()
    {
        var repeated = new byte[1024 * 1024];
        var filler = new byte[8 * 1024 * 1024];
        new Random(31415).NextBytes(repeated);
        new Random(92653).NextBytes(filler);
        var data = repeated.Concat(filler).Concat(repeated).ToArray();
        var defaultCompressor = new Laplace.Compression.Compressors.ZstdCompressor(
            CompressionMethod.ZstdHigh,
            15);
        var longDistanceCompressor = new Laplace.Compression.Compressors.ZstdCompressor(
            CompressionMethod.ZstdHigh,
            15,
            windowLog: 24,
            enableLongDistanceMatching: true);

        var defaultCompressed = defaultCompressor.Compress(data);
        var longDistanceCompressed = longDistanceCompressor.Compress(data);

        Assert.True(
            longDistanceCompressed.Length < defaultCompressed.Length - (512 * 1024),
            $"Long-distance={longDistanceCompressed.Length}, default={defaultCompressed.Length}");
        Assert.Equal(data, longDistanceCompressor.Decompress(longDistanceCompressed, data.Length));
    }

    [Theory]
    [InlineData("%1")]
    [InlineData("%V")]
    [SupportedOSPlatform("windows")]
    public void UltraRatioShellVerb_UsesExtremeVerifiedCompression(string targetPlaceholder)
    {
        var verbs = ShellIntegrationManager.BuildCreateVerbs("\"laplace.exe\"", "\"laplace-gui.exe\"", targetPlaceholder);
        var ultra = Assert.Single(verbs, verb => verb.Title == "Ultra Ratio");

        Assert.Equal("create_ultra_ratio", ultra.Name);
        Assert.Equal($"\"laplace-gui.exe\" --compress-beside \"{targetPlaceholder}\" --mode extreme --verify", ultra.Command);
    }

    [Fact]
    public void RarCompressedMode_UsesRar5SolidMaxCompression()
    {
        var args = RarArchiveWriter.BuildRarArguments(
            "archive.rar",
            new CreateArchiveOptions
            {
                Mode = CompressionMode.Compressed,
                SolidMode = SolidMode.Auto,
                Threads = 64
            },
            ["src"]);

        Assert.Contains("-ma5", args);
        Assert.Contains("-m5", args);
        Assert.Contains("-s", args);
        Assert.Contains("-md256m", args);
        Assert.Contains("-mt32", args);
        Assert.DoesNotContain("-s-", args);
    }

    [Fact]
    public void RarSolidOff_DisablesSolidArchive()
    {
        var args = RarArchiveWriter.BuildRarArguments(
            "archive.rar",
            new CreateArchiveOptions
            {
                Mode = CompressionMode.Compressed,
                SolidMode = SolidMode.Off,
                Threads = 4
            },
            ["src"]);

        Assert.Contains("-s-", args);
        Assert.DoesNotContain("-s", args);
        Assert.Contains("-mt4", args);
    }

    [Fact]
    public void RarVolumeSize_UsesNativeMultiVolumeSwitch()
    {
        var args = RarArchiveWriter.BuildRarArguments(
            "archive.rar",
            new CreateArchiveOptions
            {
                VolumeSizeBytes = 700L * 1024 * 1024
            },
            ["src"]);

        Assert.Contains("-v734003200b", args);
    }

    [Fact]
    public void SevenZipVolumeSize_UsesNativeMultiVolumeSwitch()
    {
        var args = SevenZipArchiveWriter.BuildSevenZipArguments(
            "archive.7z",
            new CreateArchiveOptions
            {
                VolumeSizeBytes = 64L * 1024 * 1024
            },
            ["src"]);

        Assert.Contains("-v67108864b", args);
    }

    [Fact]
    public async Task ZipVolumeSize_IsRejectedClearly()
    {
        var archives = new UniversalArchiveService(new CompressorRegistry());
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => archives.CompressAsync(
            [],
            "archive.zip",
            new CreateArchiveOptions
            {
                VolumeSizeBytes = 1024
            }));

        Assert.Contains("Multi-volume ZIP", exception.Message);
    }

    [Fact]
    public void ArchiveVolumePathHelper_FindsSortsAndDeletesOnlyNumericVolumes()
    {
        var root = CreateTempFolder();
        try
        {
            var outputPath = Path.Combine(root, "backup.7z");
            var volume1 = $"{outputPath}.001";
            var volume2 = $"{outputPath}.002";
            var unrelated = $"{outputPath}.notes";
            File.WriteAllText(volume2, "two");
            File.WriteAllText(unrelated, "keep");
            File.WriteAllText(volume1, "one");

            Assert.Equal([volume1, volume2], ArchiveVolumePathHelper.FindVolumes(outputPath));

            ArchiveVolumePathHelper.DeleteExistingVolumes(outputPath);

            Assert.False(File.Exists(volume1));
            Assert.False(File.Exists(volume2));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Blosc2Compressor_RoundTripsStructuredBinaryData()
    {
        var data = new byte[256 * 1024];
        for (var i = 0; i < data.Length; i += 8)
        {
            BitConverter.GetBytes(i / 8).CopyTo(data, i);
        }

        var compressor = new CompressorRegistry().GetCompressor(CompressionMethod.Blosc2);
        var compressed = compressor.Compress(data);
        var decompressed = compressor.Decompress(compressed, data.Length);

        Assert.True(compressed.Length < data.Length);
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void LzmaCompressor_RoundTripsTextLikeData()
    {
        var data = System.Text.Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, Enumerable.Repeat("laplace true lzma block", 2000)));

        var compressor = new CompressorRegistry().GetCompressor(CompressionMethod.LzmaMax);
        var compressed = compressor.Compress(data);
        var decompressed = compressor.Decompress(compressed, data.Length);

        Assert.True(compressed.Length > 5);
        Assert.NotEqual(0x28B52FFDu, BitConverter.ToUInt32(compressed, 0));
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public async Task ExtremeLpc_LongDistanceReuse_BeatsCompressedModeAndRoundTrips()
    {
        var root = CreateTempFolder();
        try
        {
            var sourcePath = Path.Combine(root, "distant-reuse.bin");
            var compressedPath = Path.Combine(root, "compressed.lpc");
            var extremePath = Path.Combine(root, "extreme.lpc");
            var extractPath = Path.Combine(root, "out");
            var repeated = new byte[3 * 1024 * 1024];
            var filler = new byte[18 * 1024 * 1024];
            new Random(12345).NextBytes(repeated);
            new Random(67890).NextBytes(filler);
            await using (var output = File.Create(sourcePath))
            {
                await output.WriteAsync(repeated);
                await output.WriteAsync(filler);
                await output.WriteAsync(repeated);
            }

            var service = new UniversalArchiveService(new CompressorRegistry());
            await service.CompressAsync([sourcePath], compressedPath, new CreateArchiveOptions
            {
                Mode = CompressionMode.Compressed,
                BlockSizeBytes = 8 * 1024 * 1024,
                VerifyAfterCompression = false
            });
            await service.CompressAsync([sourcePath], extremePath, new CreateArchiveOptions
            {
                Mode = CompressionMode.Extreme,
                AvailableCompressionMemoryBytes = 1024L * 1024 * 1024,
                VerifyAfterCompression = false
            });

            Assert.True(
                new FileInfo(extremePath).Length < new FileInfo(compressedPath).Length - (1024 * 1024),
                $"Extreme={new FileInfo(extremePath).Length}, compressed={new FileInfo(compressedPath).Length}");
            Assert.Equal(8, new ArchiveReader().ReadHeaderOnly(extremePath).FormatVersion);
            Assert.Contains(
                new ArchiveReader().Read(extremePath).BlockEntries,
                block => block.CompressionMethod == CompressionMethod.ZstdHigh);

            await service.ExtractAsync(extremePath, extractPath, new ExtractArchiveOptions { Overwrite = true });
            Assert.Equal(
                await File.ReadAllBytesAsync(sourcePath),
                await File.ReadAllBytesAsync(Path.Combine(extractPath, "distant-reuse.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtremeLpc_HighEntropyDataFallsBackToRaw()
    {
        var root = CreateTempFolder();
        try
        {
            var sourcePath = Path.Combine(root, "random.bin");
            var archivePath = Path.Combine(root, "random.lpc");
            var data = new byte[1024 * 1024];
            new Random(24680).NextBytes(data);
            await File.WriteAllBytesAsync(sourcePath, data);

            var archive = await new ArchiveWriter(new CompressorRegistry()).CreateAsync(
                [sourcePath],
                archivePath,
                new CreateArchiveOptions
                {
                    Mode = CompressionMode.Extreme,
                    AvailableCompressionMemoryBytes = 256L * 1024 * 1024,
                    VerifyAfterCompression = false
                });

            Assert.All(archive.BlockEntries, block => Assert.True(block.IsRaw));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtremeMode_RejectsNonLpcOutput()
    {
        var service = new UniversalArchiveService(new CompressorRegistry());
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => service.CompressAsync(
            [],
            "archive.7z",
            new CreateArchiveOptions { Mode = CompressionMode.Extreme }));

        Assert.Contains("LPC archives only", exception.Message);
    }

    [Fact]
    public void PathSecurity_BlocksTraversal()
    {
        var destination = Path.Combine(Path.GetTempPath(), "laplace-security-test");
        var unsafePath = "../../evil.exe";
        Assert.Throws<InvalidDataException>(() => PathSecurity.EnsureSafeExtractionPath(destination, unsafePath));
    }

    [Fact]
    public void PathSecurity_BlocksAbsolutePath()
    {
        var destination = Path.Combine(Path.GetTempPath(), "laplace-security-test");
        Assert.Throws<InvalidDataException>(() => PathSecurity.EnsureSafeExtractionPath(destination, @"C:\Windows\evil.exe"));
    }

    [Theory]
    [InlineData("/tmp/evil.txt")]
    [InlineData(@"\Windows\evil.txt")]
    [InlineData(@"\\server\share\evil.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("folder/file.txt:Zone.Identifier")]
    [InlineData("folder/CON.txt")]
    [InlineData("folder/name./file.txt")]
    [InlineData("folder/name /file.txt")]
    [InlineData("folder//file.txt")]
    [InlineData("folder/./file.txt")]
    public void PathSecurity_BlocksUnsafeWindowsExtractionNames(string unsafePath)
    {
        var destination = Path.Combine(Path.GetTempPath(), "laplace-security-test");
        Assert.Throws<InvalidDataException>(() => PathSecurity.EnsureSafeExtractionPath(destination, unsafePath));
    }

    [Fact]
    public void PathSecurity_AllowsNormalRelativePath()
    {
        var destination = Path.Combine(Path.GetTempPath(), "laplace-security-test");
        var path = PathSecurity.EnsureSafeExtractionPath(destination, "folder/subfolder/file.txt");
        Assert.EndsWith(Path.Combine("folder", "subfolder", "file.txt"), path);
    }

    [Theory]
    [InlineData("folder/file*.txt")]
    [InlineData("folder/file?.txt")]
    [InlineData("folder/file<>.txt")]
    [InlineData("folder/file|pipe.txt")]
    [InlineData("folder/file\"quote.txt")]
    public void PathSecurity_BlocksInvalidFileNameCharacters(string unsafePath)
    {
        var destination = Path.Combine(Path.GetTempPath(), "laplace-security-test");
        Assert.Throws<InvalidDataException>(() => PathSecurity.EnsureSafeExtractionPath(destination, unsafePath));
    }

    [Fact]
    public void PathSecurity_EnsureNoReparsePointInPath_BlocksExistingReparsePointFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempFolder();
        try
        {
            var targetFile = Path.Combine(root, "target.txt");
            var symlinkFile = Path.Combine(root, "symlink.txt");
            File.WriteAllText(targetFile, "target content");

            try
            {
                File.CreateSymbolicLink(symlinkFile, targetFile);
            }
            catch
            {
                // Skip the test if Developer Mode is disabled or system policy prevents symlink creation
                return;
            }

            Assert.Throws<InvalidDataException>(() => PathSecurity.EnsureNoReparsePointInPath(root, symlinkFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Corruption_IsDetectedByTestCommandLogic()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "data.txt");
        await File.WriteAllTextAsync(sourceFile, string.Join("", Enumerable.Repeat("ABCDEF", 10000)));
        var archivePath = Path.Combine(root, "data.lpc");

        var registry = new CompressorRegistry();
        var writer = new ArchiveWriter(registry);
        await writer.CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Balanced,
            BlockSizeBytes = 4 * 1024 * 1024,
            VerifyAfterCompression = false
        });

        var archive = new ArchiveReader().Read(archivePath);
        var blockToCorrupt = Assert.Single(archive.BlockEntries);
        using (var fs = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = blockToCorrupt.DataOffset + blockToCorrupt.CompressedBlockSize / 2;
            var b = fs.ReadByte();
            fs.Position -= 1;
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        var tester = new ArchiveTester(registry);
        var result = await tester.TestAsync(archivePath);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task EncryptedLpc_WithCorrectPassword_RoundTrips()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "secret.txt");
        await File.WriteAllTextAsync(sourceFile, string.Join(Environment.NewLine, Enumerable.Repeat("encrypted lpc payload", 500)));
        var archivePath = Path.Combine(root, "secret.lpc");
        var extractPath = Path.Combine(root, "out");
        var registry = new CompressorRegistry();
        var password = new PasswordContext("correct horse battery staple");

        var writer = new ArchiveWriter(registry);
        await writer.CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Balanced,
            BlockSizeBytes = 4 * 1024 * 1024,
            Password = password,
            VerifyAfterCompression = false
        });

        var archive = new ArchiveReader().Read(archivePath);
        Assert.True(archive.Header.IsEncrypted);
        Assert.Equal(8, archive.Header.FormatVersion);
        Assert.Equal((byte)KeyDerivationAlgorithm.Argon2id, archive.Header.KeyDerivationAlgorithmId);
        Assert.Equal(CreateArchiveOptions.DefaultArgon2Iterations, archive.Header.KeyDerivationIterations);
        Assert.Equal(CreateArchiveOptions.DefaultArgon2MemoryKiB, archive.Header.KeyDerivationMemoryKiB);
        Assert.Equal(ArchiveEncryption.GeneratedSaltSizeBytes, archive.Header.EncryptionSalt.Length);

        var tester = new ArchiveTester(registry);
        Assert.True((await tester.TestAsync(archivePath, password)).Success);
        Assert.False((await tester.TestAsync(archivePath, new PasswordContext("wrong password"))).Success);

        var extractor = new ArchiveExtractor(registry);
        await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() =>
            extractor.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true }));

        await extractor.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
        {
            Overwrite = true,
            Password = password
        });

        Assert.Equal(await File.ReadAllTextAsync(sourceFile), await File.ReadAllTextAsync(Path.Combine(extractPath, "secret.txt")));
    }

    [Fact]
    public async Task EncryptedLpc_WithPasswordAndKeyfile_RequiresBothFactors()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "secret.txt");
        await File.WriteAllTextAsync(sourceFile, "two factor payload");
        var archivePath = Path.Combine(root, "secret-2fa.lpc");
        var extractPath = Path.Combine(root, "out");
        var keyfileBytes = "laplace-key-material"u8.ToArray();
        var password = new PasswordContext("correct horse battery staple", SHA256.HashData(keyfileBytes));
        var registry = new CompressorRegistry();

        var writer = new ArchiveWriter(registry);
        await writer.CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Password = password,
            VerifyAfterCompression = false
        });

        var tester = new ArchiveTester(registry);
        Assert.True((await tester.TestAsync(archivePath, password)).Success);
        Assert.False((await tester.TestAsync(archivePath, new PasswordContext("correct horse battery staple"))).Success);
        Assert.False((await tester.TestAsync(archivePath, new PasswordContext(password: null, keyfileHash: SHA256.HashData(keyfileBytes)))).Success);

        var extractor = new ArchiveExtractor(registry);
        await extractor.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
        {
            Overwrite = true,
            Password = password
        });

        Assert.Equal(await File.ReadAllTextAsync(sourceFile), await File.ReadAllTextAsync(Path.Combine(extractPath, "secret.txt")));
    }

    [Theory]
    [InlineData(CreateArchiveOptions.MinimumKeyDerivationIterations - 1)]
    [InlineData(CreateArchiveOptions.MaximumKeyDerivationIterations + 1)]
    public async Task EncryptedLpc_RejectsUnsafeKeyDerivationIterations(int iterations)
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "secret.txt");
        await File.WriteAllTextAsync(sourceFile, "secret");
        var archivePath = Path.Combine(root, "secret.lpc");

        var writer = new ArchiveWriter(new CompressorRegistry());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
            {
                Password = new PasswordContext("correct horse battery staple"),
                KeyDerivationAlgorithm = KeyDerivationAlgorithm.Pbkdf2Sha256,
                KeyDerivationIterations = iterations,
                VerifyAfterCompression = false
            }));
    }

    [Fact]
    public async Task EncryptedLpc_ExplicitPbkdf2_RoundTripsWithVersionedKdf()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "legacy-kdf.txt");
        await File.WriteAllTextAsync(sourceFile, "PBKDF2 compatibility payload");
        var archivePath = Path.Combine(root, "legacy-kdf.lpc");
        var extractPath = Path.Combine(root, "out");
        var password = new PasswordContext("compatibility password");
        var registry = new CompressorRegistry();

        await new ArchiveWriter(registry).CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Password = password,
            KeyDerivationAlgorithm = KeyDerivationAlgorithm.Pbkdf2Sha256,
            KeyDerivationIterations = CreateArchiveOptions.MinimumKeyDerivationIterations,
            VerifyAfterCompression = false
        });

        var archive = new ArchiveReader().Read(archivePath);
        Assert.Equal(8, archive.Header.FormatVersion);
        Assert.Equal((byte)KeyDerivationAlgorithm.Pbkdf2Sha256, archive.Header.KeyDerivationAlgorithmId);
        Assert.True((await new ArchiveTester(registry).TestAsync(archivePath, password)).Success);

        await new ArchiveExtractor(registry).ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
        {
            Overwrite = true,
            Password = password
        });
        Assert.Equal("PBKDF2 compatibility payload", await File.ReadAllTextAsync(Path.Combine(extractPath, "legacy-kdf.txt")));
    }

    [Fact]
    public async Task MetadataEncryptedLpc_HidesTablesAndRequiresPasswordForListing()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "hidden-name.txt");
        await File.WriteAllTextAsync(sourceFile, "metadata encryption payload");
        var archivePath = Path.Combine(root, "hidden.lpc");
        var password = new PasswordContext("metadata password");
        var registry = new CompressorRegistry();

        await new ArchiveWriter(registry).CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Password = password,
            EncryptMetadata = true,
            VerifyAfterCompression = false
        });

        var reader = new ArchiveReader();
        var header = reader.ReadHeaderOnly(archivePath);
        Assert.Equal(8, header.FormatVersion);
        Assert.True(header.IsMetadataEncrypted);
        Assert.Throws<ArchivePasswordRequiredException>(() => reader.Read(archivePath));
        Assert.Throws<ArchivePasswordException>(() => reader.Read(archivePath, new PasswordContext("wrong")));
        Assert.Contains(reader.Read(archivePath, password).FileEntries, entry => entry.RelativePath == "hidden-name.txt");
        Assert.Equal(-1, File.ReadAllBytes(archivePath).AsSpan().IndexOf(Encoding.UTF8.GetBytes("hidden-name.txt")));
    }

    [Fact]
    public async Task EncryptedZip_WithCorrectPassword_RoundTrips()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "payload");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "secret.txt"), "zip secret zip secret zip secret");
        var archivePath = Path.Combine(root, "secret.zip");
        var extractPath = Path.Combine(root, "out");
        var service = new UniversalArchiveService(new CompressorRegistry());
        var password = new PasswordContext("zip-password");

        await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
        {
            Password = password,
            VerifyAfterCompression = false
        });

        var info = service.Info(archivePath, password);
        Assert.Equal("ZIP", info.Format);
        Assert.True(info.IsEncrypted);
        Assert.True((await service.TestAsync(archivePath, password)).Success);
        Assert.False((await service.TestAsync(archivePath, new PasswordContext("wrong"))).Success);

        await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
        {
            Overwrite = true,
            Password = password
        });

        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(sourceDir, "secret.txt")),
            await File.ReadAllTextAsync(Path.Combine(extractPath, "payload", "secret.txt")));
    }

    [Fact]
    public async Task SevenZip_CompressThenExtract_RoundTrips()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "payload");
        var nestedDir = Path.Combine(sourceDir, "nested");
        Directory.CreateDirectory(nestedDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "alpha.txt"), string.Join(Environment.NewLine, Enumerable.Repeat("alpha", 200)));
        await File.WriteAllTextAsync(Path.Combine(nestedDir, "beta.txt"), string.Join(Environment.NewLine, Enumerable.Repeat("beta", 200)));
        var archivePath = Path.Combine(root, "payload.7z");
        var extractPath = Path.Combine(root, "out");
        var service = new UniversalArchiveService(new CompressorRegistry());

        await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Maximum,
            VerifyAfterCompression = false
        });

        var info = service.Info(archivePath);
        Assert.Equal("7Z", info.Format);
        Assert.True(info.CompressedSize > 0);
        Assert.True((await service.TestAsync(archivePath)).Success);

        await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true });

        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(sourceDir, "alpha.txt")),
            await File.ReadAllTextAsync(Path.Combine(extractPath, "payload", "alpha.txt")));
        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(nestedDir, "beta.txt")),
            await File.ReadAllTextAsync(Path.Combine(extractPath, "payload", "nested", "beta.txt")));
    }

    [Fact]
    public async Task Lpc_SelectedDirectoryExtraction_ExtractsDescendantsOnly()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "payload");
        var selectedDir = Path.Combine(sourceDir, "selected");
        var skippedDir = Path.Combine(sourceDir, "skipped");
        Directory.CreateDirectory(selectedDir);
        Directory.CreateDirectory(skippedDir);
        await File.WriteAllTextAsync(Path.Combine(selectedDir, "keep.txt"), "keep this file");
        await File.WriteAllTextAsync(Path.Combine(skippedDir, "skip.txt"), "skip this file");
        var archivePath = Path.Combine(root, "payload.lpc");
        var extractPath = Path.Combine(root, "out");
        var service = new UniversalArchiveService(new CompressorRegistry());

        await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Balanced,
            VerifyAfterCompression = false
        });
        var selectedEntry = service.List(archivePath).Single(x => x.IsDirectory && x.Path == "payload/selected");

        await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
        {
            Overwrite = true,
            SelectedEntryIds = new HashSet<long> { selectedEntry.Id }
        });

        Assert.True(File.Exists(Path.Combine(extractPath, "payload", "selected", "keep.txt")));
        Assert.False(File.Exists(Path.Combine(extractPath, "payload", "skipped", "skip.txt")));
    }

    [Fact]
    public async Task SolidLpc_CompressThenExtract_RoundTripsAndUsesFormatV4()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "payload");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "alpha.txt"), new string('A', 180_000));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "beta.txt"), new string('B', 180_000));
        var archivePath = Path.Combine(root, "payload-solid.lpc");
        var extractPath = Path.Combine(root, "out");
        var service = new UniversalArchiveService(new CompressorRegistry());

        await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Maximum,
            SolidMode = SolidMode.On,
            BlockSizeBytes = 256 * 1024,
            VerifyAfterCompression = false
        });

        var archive = new ArchiveReader().Read(archivePath);
        Assert.True(archive.Header.IsSolid);
        Assert.Equal(8, archive.Header.FormatVersion);
        Assert.All(archive.BlockEntries, block => Assert.Equal(-1, block.OwningFileEntryId));
        Assert.Contains(archive.FileEntries, entry => !entry.IsDirectory && entry.BlockCount > 1);

        await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true, VerifyChecksums = true });

        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(sourceDir, "alpha.txt")),
            await File.ReadAllTextAsync(Path.Combine(extractPath, "payload", "alpha.txt")));
        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(sourceDir, "beta.txt")),
            await File.ReadAllTextAsync(Path.Combine(extractPath, "payload", "beta.txt")));
    }

    [Fact]
    public async Task SolidLpc_SelectedFileExtraction_ExtractsOnlyChosenFile()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "payload");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "alpha.txt"), new string('A', 180_000));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "beta.txt"), new string('B', 180_000));
        var archivePath = Path.Combine(root, "payload-solid.lpc");
        var extractPath = Path.Combine(root, "out");
        var service = new UniversalArchiveService(new CompressorRegistry());

        await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Maximum,
            SolidMode = SolidMode.On,
            BlockSizeBytes = 256 * 1024,
            VerifyAfterCompression = false
        });
        var selectedEntry = service.List(archivePath).Single(x => x.Path.EndsWith("alpha.txt", StringComparison.OrdinalIgnoreCase));

        await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
        {
            Overwrite = true,
            VerifyChecksums = true,
            SelectedEntryIds = new HashSet<long> { selectedEntry.Id }
        });

        Assert.True(File.Exists(Path.Combine(extractPath, "payload", "alpha.txt")));
        Assert.False(File.Exists(Path.Combine(extractPath, "payload", "beta.txt")));
    }

    [Fact]
    public async Task SolidLpc_UsesConfiguredParallelCompressionAndKeepsBlockOrder()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "parallel");
        Directory.CreateDirectory(sourceDir);
        for (var i = 0; i < 12; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(sourceDir, $"part-{i:00}.txt"), new string((char)('A' + i), 96 * 1024));
        }

        var trackingRegistry = new TrackingCompressorRegistry();
        var archivePath = Path.Combine(root, "parallel.lpc");
        var archive = await new ArchiveWriter(trackingRegistry).CreateAsync([sourceDir], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Fast,
            SolidMode = SolidMode.On,
            BlockSizeBytes = 64 * 1024,
            Threads = 4,
            VerifyAfterCompression = false
        });

        Assert.True(trackingRegistry.MaximumConcurrentCompression > 1);
        Assert.Equal(
            Enumerable.Range(0, archive.BlockEntries.Count).Select(value => (long)value),
            archive.BlockEntries.Select(block => block.BlockId));
        Assert.Equal(
            archive.BlockEntries.OrderBy(block => block.OriginalStreamOffset).Select(block => block.BlockId),
            archive.BlockEntries.Select(block => block.BlockId));
        Assert.True((await new ArchiveTester(trackingRegistry).TestAsync(archivePath)).Success);
    }

    [Fact]
    public async Task CreateIndependent_UsesParallelCompression()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "parallel_ind");
        Directory.CreateDirectory(sourceDir);
        for (var i = 0; i < 12; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(sourceDir, $"part-{i:00}.txt"), new string((char)('A' + i), 96 * 1024));
        }

        var trackingRegistry = new TrackingCompressorRegistry();
        var archivePath = Path.Combine(root, "parallel_ind.lpc");
        var archive = await new ArchiveWriter(trackingRegistry).CreateAsync([sourceDir], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Fast,
            SolidMode = SolidMode.Off,
            BlockSizeBytes = 64 * 1024,
            Threads = 4,
            VerifyAfterCompression = false
        });

        Assert.True(trackingRegistry.MaximumConcurrentCompression > 1);
        Assert.Equal(
            Enumerable.Range(0, archive.BlockEntries.Count).Select(value => (long)value),
            archive.BlockEntries.Select(block => block.BlockId));
        Assert.True((await new ArchiveTester(trackingRegistry).TestAsync(archivePath)).Success);
    }

    [Fact]
    public async Task RecoveryRecord_RepairsCorruptedPayloadShard()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "recover.txt");
        await File.WriteAllTextAsync(sourceFile, string.Concat(Enumerable.Repeat("recovery payload line\n", 20_000)));
        var archivePath = Path.Combine(root, "recover.lpc");
        var extractPath = Path.Combine(root, "out");
        var registry = new CompressorRegistry();

        await new ArchiveWriter(registry).CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Fast,
            BlockSizeBytes = 64 * 1024,
            RecoveryPercent = 25,
            VerifyAfterCompression = false
        });

        var archive = new ArchiveReader().Read(archivePath);
        Assert.Equal(8, archive.Header.FormatVersion);
        Assert.True(archive.Header.HasRecoveryRecord);
        Assert.Equal(25, archive.Header.RecoveryPercent);
        var block = archive.BlockEntries[archive.BlockEntries.Count / 2];
        await using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = block.DataOffset + Math.Max(0, block.CompressedBlockSize / 2);
            var value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0x5A));
        }

        Assert.False((await new ArchiveTester(registry).TestAsync(archivePath)).Success);
        var repairedShards = await new LpcRecoveryService().RepairAsync(archivePath);
        Assert.True(repairedShards >= 1);
        Assert.True((await new ArchiveTester(registry).TestAsync(archivePath)).Success);

        await new ArchiveExtractor(registry).ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true });
        Assert.Equal(await File.ReadAllTextAsync(sourceFile), await File.ReadAllTextAsync(Path.Combine(extractPath, "recover.txt")));

        await using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = archive.Header.RecoveryRecordOffset + 40;
            var value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0xA5));
        }
        Assert.False((await new ArchiveTester(registry).TestAsync(archivePath)).Success);
    }

    [Fact]
    public async Task Zip_SelectedFileExtraction_ExtractsOnlyChosenFile()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "payload");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "alpha.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "beta.txt"), "beta");
        var archivePath = Path.Combine(root, "payload.zip");
        var extractPath = Path.Combine(root, "out");
        var service = new UniversalArchiveService(new CompressorRegistry());

        await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
        {
            VerifyAfterCompression = false
        });
        var selectedEntry = service.List(archivePath).Single(x => x.Path.EndsWith("alpha.txt", StringComparison.OrdinalIgnoreCase));

        await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
        {
            Overwrite = true,
            SelectedEntryIds = new HashSet<long> { selectedEntry.Id }
        });

        Assert.True(File.Exists(Path.Combine(extractPath, "payload", "alpha.txt")));
        Assert.False(File.Exists(Path.Combine(extractPath, "payload", "beta.txt")));
    }

    [Fact]
    public async Task LpcMutation_AddRenameDeleteCommentAndLock_WorkThroughRewrite()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "alpha.txt");
        var addedFile = Path.Combine(root, "beta.txt");
        await File.WriteAllTextAsync(sourceFile, "alpha v1");
        await File.WriteAllTextAsync(addedFile, "beta v1");
        var archivePath = Path.Combine(root, "payload.lpc");
        var extractPath = Path.Combine(root, "out");
        var registry = new CompressorRegistry();
        var service = new UniversalArchiveService(registry);
        var mutator = new LpcArchiveMutationService(registry);

        await service.CompressAsync([sourceFile], archivePath, new CreateArchiveOptions { VerifyAfterCompression = false });
        await mutator.AddAsync(archivePath, [addedFile], new MutateArchiveOptions());
        await mutator.RenameAsync(archivePath, "beta.txt", "renamed/beta.txt", new MutateArchiveOptions());
        await mutator.DeleteAsync(archivePath, ["alpha.txt"], new MutateArchiveOptions());
        await mutator.SetCommentAsync(archivePath, "managed archive", new MutateArchiveOptions());

        var info = service.Info(archivePath);
        Assert.Equal("managed archive", info.Comment);
        Assert.False(info.IsLocked);
        Assert.Contains(service.List(archivePath), x => x.Path == "renamed/beta.txt");
        Assert.DoesNotContain(service.List(archivePath), x => x.Path == "alpha.txt");
        Assert.True((await service.TestAsync(archivePath)).Success);

        await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true });
        Assert.Equal("beta v1", await File.ReadAllTextAsync(Path.Combine(extractPath, "renamed", "beta.txt")));

        await mutator.LockAsync(archivePath, new MutateArchiveOptions());
        Assert.True(service.Info(archivePath).IsLocked);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutator.DeleteAsync(archivePath, ["renamed/beta.txt"], new MutateArchiveOptions()));
    }

    [Fact]
    public async Task LpcMutation_SolidArchive_PreservesSolidMode()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "payload");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "alpha.txt"), new string('A', 120_000));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "beta.txt"), new string('B', 120_000));
        var addedFile = Path.Combine(root, "gamma.txt");
        await File.WriteAllTextAsync(addedFile, new string('C', 120_000));
        var archivePath = Path.Combine(root, "payload-solid.lpc");
        var extractPath = Path.Combine(root, "out");
        var registry = new CompressorRegistry();
        var service = new UniversalArchiveService(registry);
        var mutator = new LpcArchiveMutationService(registry);

        await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Maximum,
            SolidMode = SolidMode.On,
            BlockSizeBytes = 192 * 1024,
            VerifyAfterCompression = false
        });

        await mutator.AddAsync(archivePath, [addedFile], new MutateArchiveOptions());

        var archive = new ArchiveReader().Read(archivePath);
        Assert.True(archive.Header.IsSolid);
        Assert.Equal(8, archive.Header.FormatVersion);
        Assert.True((await service.TestAsync(archivePath)).Success);

        await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true, VerifyChecksums = true });
        Assert.Equal(new string('C', 120_000), await File.ReadAllTextAsync(Path.Combine(extractPath, "gamma.txt")));
    }

    [Fact]
    public async Task LpcMutation_Freshen_UpdatesExistingEntryOnlyWhenInputIsNewer()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "fresh.txt");
        await File.WriteAllTextAsync(sourceFile, "old");
        var archivePath = Path.Combine(root, "fresh.lpc");
        var extractPath = Path.Combine(root, "out");
        var registry = new CompressorRegistry();
        var service = new UniversalArchiveService(registry);
        var mutator = new LpcArchiveMutationService(registry);

        await service.CompressAsync([sourceFile], archivePath, new CreateArchiveOptions { VerifyAfterCompression = false });
        await Task.Delay(1100);
        await File.WriteAllTextAsync(sourceFile, "new");
        await mutator.FreshenAsync(archivePath, [sourceFile], new MutateArchiveOptions());

        await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true });
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(extractPath, "fresh.txt")));
    }

    [Fact]
    public async Task LpcMutation_EncryptedArchive_RequiresPasswordAndPreservesEncryption()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "secret.txt");
        var addedFile = Path.Combine(root, "added.txt");
        await File.WriteAllTextAsync(sourceFile, "secret");
        await File.WriteAllTextAsync(addedFile, "added");
        var archivePath = Path.Combine(root, "secret.lpc");
        var password = new PasswordContext("correct horse battery staple");
        var registry = new CompressorRegistry();
        var service = new UniversalArchiveService(registry);
        var mutator = new LpcArchiveMutationService(registry);

        await service.CompressAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Password = password,
            VerifyAfterCompression = false
        });

        await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() =>
            mutator.AddAsync(archivePath, [addedFile], new MutateArchiveOptions()));

        await mutator.AddAsync(archivePath, [addedFile], new MutateArchiveOptions { Password = password });

        var info = service.Info(archivePath, password);
        Assert.True(info.IsEncrypted);
        Assert.Contains(service.List(archivePath, password), x => x.Path == "added.txt");
        Assert.True((await service.TestAsync(archivePath, password)).Success);
    }

    [Fact]
    public async Task SevenZip_SelectedFileExtraction_ExtractsOnlyChosenFile()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "payload");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "alpha.txt"), string.Join(Environment.NewLine, Enumerable.Repeat("alpha", 200)));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "beta.txt"), string.Join(Environment.NewLine, Enumerable.Repeat("beta", 200)));
        var archivePath = Path.Combine(root, "payload.7z");
        var extractPath = Path.Combine(root, "out");
        var service = new UniversalArchiveService(new CompressorRegistry());

        await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Maximum,
            VerifyAfterCompression = false
        });
        var selectedEntry = service.List(archivePath).Single(x => x.Path.EndsWith("alpha.txt", StringComparison.OrdinalIgnoreCase));

        await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
        {
            Overwrite = true,
            SelectedEntryIds = new HashSet<long> { selectedEntry.Id }
        });

        Assert.True(File.Exists(Path.Combine(extractPath, "payload", "alpha.txt")));
        Assert.False(File.Exists(Path.Combine(extractPath, "payload", "beta.txt")));
    }

    [Fact]
    public void DetectReadKind_UsesSevenZipAndRarMagicBeforeExtension()
    {
        var root = CreateTempFolder();
        var sevenZipPath = Path.Combine(root, "misnamed.lpc");
        File.WriteAllBytes(sevenZipPath, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04]);
        var rarPath = Path.Combine(root, "misnamed.zip");
        File.WriteAllBytes(rarPath, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);
        var cabPath = Path.Combine(root, "sample.cab");

        Assert.Equal(SupportedArchiveKind.SevenZip, ArchiveFormatDetector.DetectReadKind(sevenZipPath));
        Assert.Equal(SupportedArchiveKind.Rar, ArchiveFormatDetector.DetectReadKind(rarPath));
        Assert.Equal(SupportedArchiveKind.External, ArchiveFormatDetector.DetectReadKind(cabPath));
    }

    [WindowsOnlyFact]
    public async Task WindowsNativeArchiveHandler_ExtractsZipArchive()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "payload");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "native.txt"), "windows native extraction");
        var archivePath = Path.Combine(root, "payload.zip");
        var extractPath = Path.Combine(root, "native-out");
        await using (var file = File.Create(archivePath))
        using (var zip = new ZipOutputStream(file))
        {
            await zip.PutNextEntryAsync(new ZipEntry("payload/"), CancellationToken.None);
            await zip.CloseEntryAsync(CancellationToken.None);
            await zip.PutNextEntryAsync(new ZipEntry("payload/native.txt"), CancellationToken.None);
            var bytes = "windows native extraction"u8.ToArray();
            await zip.WriteAsync(bytes);
            await zip.CloseEntryAsync(CancellationToken.None);
            await zip.FinishAsync(CancellationToken.None);
        }

        var native = new WindowsNativeArchiveHandler();
        if (!native.IsAvailable)
        {
            return;
        }

        await native.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true });

        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(sourceDir, "native.txt")),
            await File.ReadAllTextAsync(Path.Combine(extractPath, "payload", "native.txt")));
    }

    [WindowsOnlyFact]
    public async Task UniversalArchiveService_FallsBackToWindowsNativeExtraction_WhenSharpCompressCannotReadArchive()
    {
        var root = CreateTempFolder();
        var sourceDir = Path.Combine(root, "payload");
        var nestedDir = Path.Combine(sourceDir, "nested");
        Directory.CreateDirectory(nestedDir);
        await File.WriteAllTextAsync(
            Path.Combine(sourceDir, "native.txt"),
            "windows native fallback extraction" + Environment.NewLine + string.Concat(Enumerable.Repeat("abc123 ", 2000)));
        await File.WriteAllTextAsync(Path.Combine(nestedDir, "data.csv"), string.Join(Environment.NewLine, ["id,value", "1,alpha", "2,beta", "3,gamma"]));
        await File.WriteAllBytesAsync(Path.Combine(nestedDir, "bytes.bin"), Enumerable.Range(0, 256).Select(x => (byte)x).ToArray());
        var archivePath = Path.Combine(root, "payload.tar.zst");
        var extractPath = Path.Combine(root, "native-fallback-out");
        var service = new UniversalArchiveService(new CompressorRegistry());

        await CreateTarZstdArchiveAsync(root, archivePath, "payload");

        await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true });

        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(sourceDir, "native.txt")),
            await File.ReadAllTextAsync(Path.Combine(extractPath, "payload", "native.txt")));
        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(nestedDir, "data.csv")),
            await File.ReadAllTextAsync(Path.Combine(extractPath, "payload", "nested", "data.csv")));
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(nestedDir, "bytes.bin")),
            await File.ReadAllBytesAsync(Path.Combine(extractPath, "payload", "nested", "bytes.bin")));
    }

    [Fact]
    public async Task ZipExtraction_BlocksTraversalEntry()
    {
        var root = CreateTempFolder();
        var archivePath = Path.Combine(root, "evil.zip");
        await using (var file = File.Create(archivePath))
        using (var zip = new ZipOutputStream(file))
        {
            await zip.PutNextEntryAsync(new ZipEntry("../evil.txt"), CancellationToken.None);
            var bytes = "evil"u8.ToArray();
            await zip.WriteAsync(bytes);
            await zip.CloseEntryAsync(CancellationToken.None);
            await zip.FinishAsync(CancellationToken.None);
        }

        var service = new UniversalArchiveService(new CompressorRegistry());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ExtractAsync(archivePath, Path.Combine(root, "out"), new ExtractArchiveOptions { Overwrite = true }));
        Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
    }

    [Theory]
    [InlineData("payload/file.txt:Zone.Identifier")]
    [InlineData("payload/CON.txt")]
    public async Task ZipExtraction_BlocksUnsafeWindowsEntryNames(string entryName)
    {
        var root = CreateTempFolder();
        var archivePath = Path.Combine(root, "evil-name.zip");
        await using (var file = File.Create(archivePath))
        using (var zip = new ZipOutputStream(file))
        {
            await zip.PutNextEntryAsync(new ZipEntry(entryName), CancellationToken.None);
            var bytes = "evil"u8.ToArray();
            await zip.WriteAsync(bytes);
            await zip.CloseEntryAsync(CancellationToken.None);
            await zip.FinishAsync(CancellationToken.None);
        }

        var service = new UniversalArchiveService(new CompressorRegistry());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ExtractAsync(archivePath, Path.Combine(root, "out"), new ExtractArchiveOptions { Overwrite = true }));
    }

    private static string CreateTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"laplace-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static async Task CreateTarZstdArchiveAsync(string workingDirectory, string archivePath, string inputPath)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "tar.exe",
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--zstd");
        startInfo.ArgumentList.Add("-cf");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add(inputPath);

        using var process = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start tar.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"tar.exe failed with exit code {process.ExitCode}: {await stdout} {await stderr}");
        }
    }

    private sealed class WindowsOnlyFactAttribute : FactAttribute
    {
        public WindowsOnlyFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Windows native archive extraction is only available on Windows.";
            }
        }
    }

    [Fact]
    public async Task SfxArchive_RoundTripsAndExtractsCorrectly()
    {
        var baseDir = AppContext.BaseDirectory;
        var dummyGuiPath = Path.Combine(baseDir, "laplace-gui.exe");
        var dummyStubPath = Path.Combine(baseDir, "laplace-sfx-stub.exe");
        var dummyGuiBytes = new byte[1024];
        new Random(42).NextBytes(dummyGuiBytes);
        await File.WriteAllBytesAsync(dummyGuiPath, dummyGuiBytes);
        await File.WriteAllBytesAsync(dummyStubPath, dummyGuiBytes);

        var root = CreateTempFolder();
        try
        {
            var sourceDir = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceDir);
            var file1 = Path.Combine(sourceDir, "test1.txt");
            var file2 = Path.Combine(sourceDir, "test2.txt");
            await File.WriteAllTextAsync(file1, "Hello from file 1! Lorem ipsum dolor sit amet.");
            await File.WriteAllTextAsync(file2, "Hello from file 2! Some more content here.");

            var sfxPath = Path.Combine(root, "archive.exe");

            var service = new UniversalArchiveService(new CompressorRegistry());
            var writeOptions = new CreateArchiveOptions
            {
                Mode = CompressionMode.Balanced,
                VerifyAfterCompression = true
            };
            await service.CompressAsync([sourceDir], sfxPath, writeOptions);

            Assert.True(File.Exists(sfxPath));
            Assert.True(LpcSfxHelper.IsSfxFile(sfxPath));

            var sfxBytes = await File.ReadAllBytesAsync(sfxPath);
            Assert.True(sfxBytes.Length > dummyGuiBytes.Length + 16);

            var stubHeader = new byte[dummyGuiBytes.Length];
            Array.Copy(sfxBytes, 0, stubHeader, 0, dummyGuiBytes.Length);
            Assert.True(stubHeader.SequenceEqual(dummyGuiBytes));

            var offset = BitConverter.ToInt64(sfxBytes, sfxBytes.Length - 16);
            Assert.Equal(dummyGuiBytes.Length, offset);

            var signature = System.Text.Encoding.ASCII.GetString(sfxBytes, sfxBytes.Length - 8, 8);
            Assert.Equal("SFXLPC!!", signature);

            var extractDir = Path.Combine(root, "extracted");
            var extractOptions = new ExtractArchiveOptions
            {
                Overwrite = true,
                VerifyChecksums = true
            };
            await service.ExtractAsync(sfxPath, extractDir, extractOptions);

            var extractedFile1 = Path.Combine(extractDir, "source", "test1.txt");
            var extractedFile2 = Path.Combine(extractDir, "source", "test2.txt");

            Assert.True(File.Exists(extractedFile1));
            Assert.True(File.Exists(extractedFile2));
            Assert.Equal("Hello from file 1! Lorem ipsum dolor sit amet.", await File.ReadAllTextAsync(extractedFile1));
            Assert.Equal("Hello from file 2! Some more content here.", await File.ReadAllTextAsync(extractedFile2));
        }
        finally
        {
            try { File.Delete(dummyGuiPath); } catch {}
            try { File.Delete(dummyStubPath); } catch {}
            try { Directory.Delete(root, recursive: true); } catch {}
        }
    }

    private sealed class TrackingCompressorRegistry : ICompressorRegistry
    {
        private readonly CompressorRegistry _inner = new();
        private readonly TrackingCompressor _tracking;

        public TrackingCompressorRegistry()
        {
            _tracking = new TrackingCompressor(_inner.GetCompressor(CompressionMethod.Lz4Fast));
        }

        public int MaximumConcurrentCompression => _tracking.MaximumConcurrentCompression;

        public IBlockCompressor GetCompressor(CompressionMethod method)
            => method == CompressionMethod.Lz4Fast ? _tracking : _inner.GetCompressor(method);

        public IBlockCompressor GetLzmaCompressor(int dictionarySizeBytes, int fastBytes)
            => _inner.GetLzmaCompressor(dictionarySizeBytes, fastBytes);

        public IBlockCompressor GetZstdCompressor(CompressionMethod method, int level, int windowLog, bool enableLongDistanceMatching)
            => _inner.GetZstdCompressor(method, level, windowLog, enableLongDistanceMatching);
    }

    private sealed class TrackingCompressor : IBlockCompressor
    {
        private readonly IBlockCompressor _inner;
        private int _activeCompression;
        private int _maximumConcurrentCompression;

        public TrackingCompressor(IBlockCompressor inner)
        {
            _inner = inner;
        }

        public CompressionMethod Method => _inner.Method;
        public int Level => _inner.Level;
        public int MaximumConcurrentCompression => Volatile.Read(ref _maximumConcurrentCompression);

        public byte[] Compress(ReadOnlySpan<byte> data)
        {
            var active = Interlocked.Increment(ref _activeCompression);
            UpdateMaximum(active);
            try
            {
                Thread.Sleep(25);
                return _inner.Compress(data);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCompression);
            }
        }

        public byte[] Decompress(ReadOnlySpan<byte> data, int expectedDecompressedSize)
            => _inner.Decompress(data, expectedDecompressedSize);

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentCompression);
                if (candidate <= current ||
                    Interlocked.CompareExchange(ref _maximumConcurrentCompression, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }

    [Fact]
    public async Task NativeLpc_MultiVolume_RoundTrip_Verify_Repair()
    {
        var root = CreateTempFolder();
        var sourceFile = Path.Combine(root, "large.bin");
        // Create 2.5MB random (uncompressible) file
        var randomBytes = new byte[2500000];
        Random.Shared.NextBytes(randomBytes);
        await File.WriteAllBytesAsync(sourceFile, randomBytes);

        var archivePath = Path.Combine(root, "multi.lpc");
        var extractPath = Path.Combine(root, "out");

        var registry = new CompressorRegistry();
        var writer = new ArchiveWriter(registry);
        
        // 1. Create multi-volume archive (1 MB volumes)
        await writer.CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
        {
            Mode = CompressionMode.Balanced,
            BlockSizeBytes = 256 * 1024,
            VolumeSizeBytes = 1024 * 1024,
            RecoveryPercent = 10, // 10% recovery record
            VerifyAfterCompression = false
        });

        // Verify volume files exist
        var vol1 = archivePath + ".001";
        var vol2 = archivePath + ".002";
        Assert.True(File.Exists(vol1), "Volume 1 should exist");
        Assert.True(File.Exists(vol2), "Volume 2 should exist");

        // 2. List entries using Volume 1 path
        var reader = new ArchiveReader();
        var entries = reader.Read(vol1).FileEntries;
        Assert.Single(entries);
        Assert.Equal("large.bin", entries[0].RelativePath);

        // 3. Extract and verify content
        var extractor = new ArchiveExtractor(registry);
        await extractor.ExtractAsync(vol1, extractPath, new ExtractArchiveOptions
        {
            Overwrite = true,
            VerifyChecksums = true
        });

        var extractedFile = Path.Combine(extractPath, "large.bin");
        Assert.True(File.Exists(extractedFile));
        Assert.Equal(randomBytes, await File.ReadAllBytesAsync(extractedFile));

        // 4. Validate recovery record passes
        var recoveryService = new LpcRecoveryService();
        await recoveryService.ValidateRecordAsync(vol1);

        // 5. Corrupt volume 2 and verify validation detects it
        var vol2Bytes = await File.ReadAllBytesAsync(vol2);
        // Find a spot inside the middle of volume 2 to corrupt (avoid headers/trailers)
        vol2Bytes[500000] ^= 0xFF;
        await File.WriteAllBytesAsync(vol2, vol2Bytes);

        // Validation should fail
        await Assert.ThrowsAsync<LaplaceArchiveException>(() => recoveryService.ValidateRecordAsync(vol1));

        // 6. Repair the archive and verify validation passes and extraction is successful
        var repairedCount = await recoveryService.RepairAsync(vol1);
        Assert.True(repairedCount > 0, "Should have repaired at least one stripe/shard");

        await recoveryService.ValidateRecordAsync(vol1);

        var extractPath2 = Path.Combine(root, "out2");
        await extractor.ExtractAsync(vol1, extractPath2, new ExtractArchiveOptions
        {
            Overwrite = true,
            VerifyChecksums = true
        });
        var extractedFile2 = Path.Combine(extractPath2, "large.bin");
        Assert.Equal(randomBytes, await File.ReadAllBytesAsync(extractedFile2));
    }

    [Fact]
    public void MultiVolumeStream_ReadInWriteMode_DoesNotCrash()
    {
        var root = CreateTempFolder();
        try
        {
            var basePath = Path.Combine(root, "write_test.lpc");
            using var stream = new MultiVolumeStream(basePath, 1024 * 1024);
            
            var data = new byte[100];
            Random.Shared.NextBytes(data);
            stream.Write(data);
            
            stream.Position = 0;
            
            var buffer = new byte[50];
            int read = stream.Read(buffer, 0, buffer.Length);
            
            Assert.Equal(50, read);
            Assert.Equal(data.AsSpan(0, 50).ToArray(), buffer);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MultiVolumeStream_SequenceGap_ThrowsEarly()
    {
        var root = CreateTempFolder();
        try
        {
            var basePath = Path.Combine(root, "gap.lpc");
            var vol1 = basePath + ".001";
            var vol2 = basePath + ".002";
            var vol3 = basePath + ".003";

            File.WriteAllText(vol1, "volume 1");
            File.WriteAllText(vol3, "volume 3");

            var ex = Assert.Throws<FileNotFoundException>(() => new MultiVolumeStream(vol1));
            Assert.Contains("Volume sequence gap detected", ex.Message);
            Assert.Contains("Volume 2 is missing", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MultiVolumeStream_PositionSeekOptimization_Works()
    {
        var root = CreateTempFolder();
        try
        {
            var basePath = Path.Combine(root, "opt.lpc");
            using (var stream = new MultiVolumeStream(basePath, 10))
            {
                var data = Encoding.UTF8.GetBytes("abcdefghijklmnopqrstuvwxyz");
                stream.Write(data);
            }

            var vol1 = basePath + ".001";
            var vol2 = basePath + ".002";
            var vol3 = basePath + ".003";
            Assert.True(File.Exists(vol1));
            Assert.True(File.Exists(vol2));
            Assert.True(File.Exists(vol3));

            using (var stream = new MultiVolumeStream(vol1))
            {
                var buffer = new byte[26];
                int read = stream.Read(buffer, 0, buffer.Length);
                Assert.Equal(26, read);
                Assert.Equal("abcdefghijklmnopqrstuvwxyz", Encoding.UTF8.GetString(buffer));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Compress_WithIncludeFilter_OnlyMatchingFilesArchived()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), "hello");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "b.log"), "world");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "c.txt"), "hello2");

            var archivePath = Path.Combine(root, "archive.lpc");
            var extractPath = Path.Combine(root, "out");

            var service = new UniversalArchiveService(new CompressorRegistry());
            await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
            {
                Mode = CompressionMode.Balanced,
                IncludePatterns = ["*.txt"]
            });

            await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true });

            Assert.True(File.Exists(Path.Combine(extractPath, "src", "a.txt")));
            Assert.True(File.Exists(Path.Combine(extractPath, "src", "c.txt")));
            Assert.False(File.Exists(Path.Combine(extractPath, "src", "b.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Compress_WithExcludeFilter_MatchingFilesOmitted()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), "hello");
            var gitDir = Path.Combine(sourceDir, ".git");
            Directory.CreateDirectory(gitDir);
            await File.WriteAllTextAsync(Path.Combine(gitDir, "config"), "config");

            var archivePath = Path.Combine(root, "archive.lpc");
            var extractPath = Path.Combine(root, "out");

            var service = new UniversalArchiveService(new CompressorRegistry());
            await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
            {
                Mode = CompressionMode.Balanced,
                ExcludePatterns = [".git/**"]
            });

            await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true });

            Assert.True(File.Exists(Path.Combine(extractPath, "src", "a.txt")));
            Assert.False(Directory.Exists(Path.Combine(extractPath, "src", ".git")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Compress_WithCombinedFilters_FiltersCorrectSubset()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), "hello");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "b.log"), "world");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "c.txt"), "hello2");

            var archivePath = Path.Combine(root, "archive.lpc");
            var extractPath = Path.Combine(root, "out");

            var service = new UniversalArchiveService(new CompressorRegistry());
            await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
            {
                Mode = CompressionMode.Balanced,
                IncludePatterns = ["*.txt", "*.log"],
                ExcludePatterns = ["*.log"]
            });

            await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions { Overwrite = true });

            Assert.True(File.Exists(Path.Combine(extractPath, "src", "a.txt")));
            Assert.True(File.Exists(Path.Combine(extractPath, "src", "c.txt")));
            Assert.False(File.Exists(Path.Combine(extractPath, "src", "b.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Compress_AllFilesExcluded_ThrowsInvalidOperationException()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), "hello");

            var archivePath = Path.Combine(root, "archive.lpc");

            var service = new UniversalArchiveService(new CompressorRegistry());
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
            {
                Mode = CompressionMode.Balanced,
                ExcludePatterns = ["*"]
            }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Extract_ContinueOnError_NonSolid_SavesValidFilesAndReportsErrors()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), "aaaa");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "b.txt"), "bbbb");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "c.txt"), "cccc");

            var archivePath = Path.Combine(root, "archive.lpc");
            var extractPath = Path.Combine(root, "out");

            var service = new UniversalArchiveService(new CompressorRegistry());
            await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
            {
                Mode = CompressionMode.Balanced,
                SolidMode = SolidMode.Off
            });

            var archive = new ArchiveReader().Read(archivePath);
            var bRecord = archive.FileEntries.First(x => x.RelativePath.EndsWith("b.txt"));
            var bBlock = archive.BlockEntries[(int)bRecord.FirstBlockIndex];

            using (var fs = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Position = bBlock.DataOffset + 2;
                fs.WriteByte(0xFF);
            }

            var extractResult = await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
            {
                Overwrite = true,
                ContinueOnError = true
            });

            Assert.True(extractResult.HasErrors);
            Assert.Equal(2, extractResult.SucceededFiles);
            Assert.Equal(1, extractResult.FailedFiles);
            Assert.Contains(extractResult.Errors, e => e.RelativePath.EndsWith("b.txt"));

            Assert.True(File.Exists(Path.Combine(extractPath, "src", "a.txt")));
            Assert.True(File.Exists(Path.Combine(extractPath, "src", "c.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Extract_ContinueOnError_Solid_ReportsFatalDecompressionErrorAndSavesPriorFiles()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), "aaaa");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "b.txt"), "bbbb");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "c.txt"), "cccc");

            var archivePath = Path.Combine(root, "archive.lpc");
            var extractPath = Path.Combine(root, "out");

            var service = new UniversalArchiveService(new CompressorRegistry());
            await service.CompressAsync([sourceDir], archivePath, new CreateArchiveOptions
            {
                Mode = CompressionMode.Balanced,
                SolidMode = SolidMode.On,
                BlockSizeBytes = 1024 * 1024
            });

            var archive = new ArchiveReader().Read(archivePath);
            var block = archive.BlockEntries[0];

            using (var fs = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Position = block.DataOffset + 10;
                fs.WriteByte(0xAA);
            }

            var extractResult = await service.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
            {
                Overwrite = true,
                ContinueOnError = true
            });

            Assert.True(extractResult.HasErrors);
            Assert.Contains(extractResult.Errors, e => e.Reason.Contains("Fatal solid decompression error"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Compress_WithDeduplicateAndCdc_DeduplicatesBlocksAndExtractsCorrectly()
    {
        var root = CreateTempFolder();
        try
        {
            var file1 = Path.Combine(root, "file1.txt");
            var file2 = Path.Combine(root, "file2.txt");

            var content = string.Join(Environment.NewLine, Enumerable.Repeat("Laplace deduplication test content", 500));
            await File.WriteAllTextAsync(file1, content);
            await File.WriteAllTextAsync(file2, content);

            var archivePath = Path.Combine(root, "dedup.lpc");
            var extractPath = Path.Combine(root, "out");

            var registry = new CompressorRegistry();
            var writer = new ArchiveWriter(registry);
            
            var options = new CreateArchiveOptions
            {
                Mode = CompressionMode.Balanced,
                SolidMode = SolidMode.Off,
                Deduplicate = true,
                UseCdc = true,
                MinChunkSize = 1024,
                AvgChunkSize = 4096,
                MaxChunkSize = 16384,
                VerifyAfterCompression = false
            };

            await writer.CreateAsync([file1, file2], archivePath, options);

            var reader = new ArchiveReader();
            var archive = reader.Read(archivePath);

            var distinctOffsets = archive.BlockEntries.Select(b => b.DataOffset).Distinct().Count();
            Assert.True(archive.BlockEntries.Count > distinctOffsets, "Blocks should share DataOffset due to deduplication.");

            var extractor = new ArchiveExtractor(registry);
            await extractor.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
            {
                Overwrite = true,
                VerifyChecksums = true
            });

            Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(extractPath, "file1.txt")));
            Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(extractPath, "file2.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CloudStreaming_ExtractsFromRemoteUrlViaHttpRangeStream()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "sample.txt");
            var content = "Cloud streaming test data block. " + string.Join(" ", Enumerable.Repeat("some text", 200));
            await File.WriteAllTextAsync(sourceFile, content);

            var archivePath = Path.Combine(root, "remote.lpc");
            var registry = new CompressorRegistry();
            var writer = new ArchiveWriter(registry);

            await writer.CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
            {
                Mode = CompressionMode.Balanced,
                BlockSizeBytes = 64 * 1024
            });

            int port = 9090;
            using var listener = new System.Net.HttpListener();
            bool bound = false;
            while (!bound && port < 9200)
            {
                try
                {
                    listener.Prefixes.Clear();
                    listener.Prefixes.Add($"http://localhost:{port}/");
                    listener.Start();
                    bound = true;
                }
                catch
                {
                    port++;
                }
            }

            if (!bound) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        var context = await listener.GetContextAsync();
                        var req = context.Request;
                        var resp = context.Response;

                        var fileBytes = File.ReadAllBytes(archivePath);

                        if (req.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                        {
                            resp.ContentLength64 = fileBytes.Length;
                            resp.Close();
                            continue;
                        }

                        if (req.Headers["Range"] != null)
                        {
                            var rangeHeader = req.Headers["Range"]!;
                            var rangeValue = rangeHeader.Substring("bytes=".Length);
                            var parts = rangeValue.Split('-');
                            long start = long.Parse(parts[0]);
                            long end = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? long.Parse(parts[1]) : fileBytes.Length - 1;

                            long length = end - start + 1;
                            resp.StatusCode = 206;
                            resp.Headers.Add("Content-Range", $"bytes {start}-{end}/{fileBytes.Length}");
                            resp.ContentLength64 = length;

                            await resp.OutputStream.WriteAsync(fileBytes, (int)start, (int)length);
                        }
                        else
                        {
                            resp.ContentLength64 = fileBytes.Length;
                            await resp.OutputStream.WriteAsync(fileBytes, 0, fileBytes.Length);
                        }
                        resp.Close();
                    }
                }
                catch { }
            });

            var url = $"http://localhost:{port}/remote.lpc";

            var reader = new ArchiveReader();
            var entries = reader.Read(url).FileEntries;
            Assert.Single(entries);
            Assert.Equal("sample.txt", entries[0].RelativePath);

            var extractPath = Path.Combine(root, "extracted_remote");
            var extractor = new ArchiveExtractor(registry);
            await extractor.ExtractAsync(url, extractPath, new ExtractArchiveOptions
            {
                Overwrite = true
            });

            Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(extractPath, "sample.txt")));
            listener.Stop();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OptionalHeaderMetadataJson_RoundTripsCorrectly()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "metadata.txt");
            await File.WriteAllTextAsync(sourceFile, "LPCv8 format upgrade metadata test");
            var archivePath = Path.Combine(root, "metadata_test.lpc");
            var registry = new CompressorRegistry();
            var service = new ArchiveWriter(registry);

            var testJson = "{\"author\":\"Antigravity\",\"version\":\"8.0\",\"custom_tag\":123}";

            await service.CreateAsync([sourceFile], archivePath, new CreateArchiveOptions
            {
                VerifyAfterCompression = false,
                OptionalHeaderMetadataJson = testJson
            });

            var header = new ArchiveReader().ReadHeaderOnly(archivePath);
            Assert.Equal(8, header.FormatVersion);
            Assert.Equal(testJson, header.OptionalHeaderMetadataJson);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}


