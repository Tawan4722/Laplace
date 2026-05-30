using Laplace.Compression;
using Laplace.Core.Exceptions;
using Laplace.Core.Enums;
using Laplace.Core.Models;
using Laplace.Core.Security;
using Laplace.Core.Services;
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
        var entries = reader.ListEntries(archivePath);
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
        Assert.Equal(2, archive.Header.FormatVersion);
        Assert.Equal(CreateArchiveOptions.DefaultKeyDerivationIterations, archive.Header.KeyDerivationIterations);
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
                KeyDerivationIterations = iterations,
                VerifyAfterCompression = false
            }));
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

        Assert.Equal(SupportedArchiveKind.SevenZip, ArchiveFormatDetector.DetectReadKind(sevenZipPath));
        Assert.Equal(SupportedArchiveKind.Rar, ArchiveFormatDetector.DetectReadKind(rarPath));
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
}
