using ICSharpCode.SharpZipLib.Zip;
using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using Laplace.Core.Security;

namespace Laplace.Core.Services;

public sealed class ZipArchiveHandler
{
    public IReadOnlyList<ArchiveEntryListing> List(string archivePath, PasswordContext? password = null)
    {
        using var zip = OpenZip(archivePath, password);
        return zip.Cast<ZipEntry>()
            .Select((entry, index) => new ArchiveEntryListing
            {
                Id = index,
                IsDirectory = entry.IsDirectory,
                OriginalSize = Math.Max(0, entry.Size),
                CompressedSize = Math.Max(0, entry.CompressedSize),
                Method = entry.CompressionMethod.ToString(),
                Path = entry.Name,
                IsEncrypted = entry.IsCrypted
            })
            .ToList();
    }

    public ArchiveSummary Info(string archivePath, PasswordContext? password = null)
    {
        var entries = List(archivePath, password);
        var files = entries.Where(x => !x.IsDirectory).ToList();
        var folders = entries.Count - files.Count;
        var originalSize = files.Sum(x => x.OriginalSize);
        var compressedSize = files.Sum(x => x.CompressedSize);
        return new ArchiveSummary
        {
            Format = "ZIP",
            FileCount = files.Count,
            FolderCount = folders,
            BlockCount = entries.Count,
            OriginalSize = originalSize,
            CompressedSize = compressedSize,
            Ratio = originalSize == 0 ? 1 : (double)compressedSize / originalSize,
            MethodsUsed = entries.Select(x => x.Method).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(),
            IsEncrypted = entries.Any(x => x.IsEncrypted),
            Notes = "ZIP metadata is normalized; integrity depends on ZIP CRC/AES authentication."
        };
    }

    public async Task ExtractAsync(
        string archivePath,
        string destinationFolder,
        ExtractArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationFolder);
        using var zip = OpenZip(archivePath, options.Password);
        var entries = zip.Cast<ZipEntry>().ToList();
        var totalBytes = entries.Where(x => x.IsFile && x.Size > 0).Sum(x => x.Size);
        long processedBytes = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsCrypted && options.Password is null)
            {
                throw new ArchivePasswordRequiredException(archivePath);
            }

            var outPath = PathSecurity.EnsureSafeExtractionPath(destinationFolder, entry.Name);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(outPath);
                continue;
            }

            var parentDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            if (!options.Overwrite && File.Exists(outPath))
            {
                throw new IOException($"File already exists: {outPath}. Use overwrite mode to replace.");
            }

            try
            {
                await using var input = zip.GetInputStream(entry);
                await using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    processedBytes += read;
                    progress?.Report(new ArchiveOperationProgress
                    {
                        CurrentItem = entry.Name,
                        ProcessedBytes = processedBytes,
                        TotalBytes = totalBytes,
                        Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                    });
                }
            }
            catch (Exception ex) when (entry.IsCrypted && IsPasswordLikeFailure(ex))
            {
                throw new ArchivePasswordException($"Invalid password or corrupted encrypted ZIP entry: {entry.Name}");
            }
        }
    }

    public async Task<ArchiveTestResult> TestAsync(string archivePath, PasswordContext? password = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var zip = OpenZip(archivePath, password);
            var files = 0;
            var entries = 0;
            var buffer = new byte[128 * 1024];
            foreach (ZipEntry entry in zip)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries++;
                if (entry.IsDirectory)
                {
                    continue;
                }

                if (entry.IsCrypted && password is null)
                {
                    return ArchiveTestResult.Failed($"Archive requires a password: {archivePath}");
                }

                await using var input = zip.GetInputStream(entry);
                while (await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
                {
                }

                files++;
            }

            return ArchiveTestResult.Ok(files, entries, "ZIP readability/integrity OK.");
        }
        catch (ZipException ex)
        {
            return ArchiveTestResult.Failed(IsPasswordLikeFailure(ex)
                ? "Invalid password or corrupted encrypted ZIP entry."
                : ex.Message);
        }
    }

    private static ZipFile OpenZip(string archivePath, PasswordContext? password)
    {
        var zip = new ZipFile(File.OpenRead(archivePath));
        if (password is not null)
        {
            zip.Password = password.Password;
        }

        return zip;
    }

    private static bool IsPasswordLikeFailure(Exception ex)
    {
        return ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("AES", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("decrypt", StringComparison.OrdinalIgnoreCase);
    }
}
