using Laplace.Core.Enums;
using Laplace.Core.Models;
using Laplace.Core.Security;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;

namespace Laplace.Core.Services;

public sealed class SevenZipArchiveWriter
{
    public async Task CreateAsync(
        IEnumerable<string> inputPaths,
        string outputArchivePath,
        CreateArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (options.Password is not null)
        {
            throw new NotSupportedException("7z creation does not support encryption in the current managed writer. Create an encrypted .lpc or .zip archive instead.");
        }

        var scanned = ArchivePathScanner.Scan(inputPaths)
            .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scanned.Count == 0)
        {
            throw new InvalidOperationException("No input files were found.");
        }

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputArchivePath));
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var totalBytes = scanned.Where(x => !x.IsDirectory).Sum(x => new FileInfo(x.FullPath).Length);
        long processedBytes = 0;

        await Task.Run(() =>
        {
            using var writer = WriterFactory.OpenWriter(
                outputArchivePath,
                ArchiveType.SevenZip,
                new SevenZipWriterOptions(CompressionType.LZMA)
                {
                    CompressionLevel = MapCompressionLevel(options.Mode)
                });

            foreach (var source in scanned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = PathSecurity.NormalizeArchivePath(source.RelativePath);
                if (source.IsDirectory)
                {
                    writer.WriteDirectory(relativePath, Directory.GetLastWriteTime(source.FullPath));
                    progress?.Report(new ArchiveOperationProgress
                    {
                        CurrentItem = relativePath,
                        ProcessedBytes = processedBytes,
                        TotalBytes = totalBytes,
                        Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                    });
                    continue;
                }

                using var input = File.OpenRead(source.FullPath);
                writer.Write(relativePath, input, File.GetLastWriteTime(source.FullPath));
                processedBytes += input.Length;
                progress?.Report(new ArchiveOperationProgress
                {
                    CurrentItem = relativePath,
                    ProcessedBytes = processedBytes,
                    TotalBytes = totalBytes,
                    Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                });
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static int MapCompressionLevel(CompressionMode mode)
    {
        return mode switch
        {
            CompressionMode.Fast => 1,
            CompressionMode.Maximum or CompressionMode.Intensive => 9,
            _ => 6
        };
    }
}
