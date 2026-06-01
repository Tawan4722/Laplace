using ICSharpCode.SharpZipLib.Zip;
using Laplace.Core.Models;
using Laplace.Core.Security;

namespace Laplace.Core.Services;

public sealed class ZipArchiveWriter
{
    public async Task CreateAsync(
        IEnumerable<string> inputPaths,
        string outputArchivePath,
        CreateArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scanned = ArchivePathScanner.Scan(inputPaths)
            .OrderBy(x => x.RelativePath.Count(c => c == '/'))
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scanned.Count == 0)
        {
            throw new InvalidOperationException("No input files or folders were found.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputArchivePath))!);
        var totalBytes = scanned.Where(x => !x.IsDirectory).Sum(x => new FileInfo(x.FullPath).Length);
        long processedBytes = 0;

        await using var file = new FileStream(outputArchivePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        using var zip = new ZipOutputStream(file)
        {
            UseZip64 = UseZip64.Dynamic
        };
        zip.SetLevel(options.Mode == Enums.CompressionMode.Fast ? 1 : options.Mode is Enums.CompressionMode.Maximum or Enums.CompressionMode.Intensive ? 9 : 6);
        if (options.Password is not null)
        {
            zip.Password = options.Password.Password;
        }

        var buffer = new byte[128 * 1024];
        foreach (var source in scanned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = PathSecurity.NormalizeArchivePath(source.RelativePath);
            if (source.IsDirectory)
            {
                if (!relativePath.EndsWith('/'))
                {
                    relativePath += "/";
                }

                var directoryEntry = new ZipEntry(relativePath)
                {
                    DateTime = Directory.GetLastWriteTime(source.FullPath)
                };
                await zip.PutNextEntryAsync(directoryEntry, cancellationToken).ConfigureAwait(false);
                await zip.CloseEntryAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var fileInfo = new FileInfo(source.FullPath);
            var entry = new ZipEntry(relativePath)
            {
                DateTime = fileInfo.LastWriteTime,
                Size = fileInfo.Length
            };
            if (options.Password is not null)
            {
                entry.AESKeySize = 256;
            }

            await zip.PutNextEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            await using var input = new FileStream(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await zip.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                processedBytes += read;
                progress?.Report(new ArchiveOperationProgress
                {
                    CurrentItem = relativePath,
                    ProcessedBytes = processedBytes,
                    TotalBytes = totalBytes,
                    Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                });
            }

            await zip.CloseEntryAsync(cancellationToken).ConfigureAwait(false);
        }

        await zip.FinishAsync(cancellationToken).ConfigureAwait(false);
    }
}
