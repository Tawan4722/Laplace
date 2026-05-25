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

    private static string CreateTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"laplace-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }
}
