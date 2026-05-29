using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using Laplace.Core.Security;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Laplace.Core.Services;

public sealed class SharpCompressArchiveHandler
{
    public IReadOnlyList<ArchiveEntryListing> List(string archivePath, PasswordContext? password = null)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, CreateReaderOptions(archivePath, password));
            var entries = new List<ArchiveEntryListing>();
            long index = 0;
            foreach (var entry in archive.Entries)
            {
                entries.Add(new ArchiveEntryListing
                {
                    Id = index++,
                    IsDirectory = entry.IsDirectory,
                    OriginalSize = Math.Max(0, entry.Size),
                    CompressedSize = Math.Max(0, entry.CompressedSize),
                    Method = entry.CompressionType.ToString(),
                    Path = NormalizeEntryName(entry.Key, archivePath),
                    IsEncrypted = entry.IsEncrypted
                });
            }

            return entries;
        }
        catch (Exception ex) when (password is null && IsPasswordLikeFailure(ex))
        {
            throw new ArchivePasswordRequiredException(archivePath);
        }
        catch (Exception ex) when (IsUnsupportedArchiveFailure(ex))
        {
            throw new NotSupportedException($"Unsupported or unreadable archive. Supported read formats: {ArchiveFormatDetector.SupportedReadFormats}", ex);
        }
    }

    public ArchiveSummary Info(string archivePath, PasswordContext? password = null)
    {
        var entries = List(archivePath, password);
        var files = entries.Where(x => !x.IsDirectory).ToList();
        var originalSize = files.Sum(x => x.OriginalSize);
        var compressedSize = files.Sum(x => x.CompressedSize);
        if (compressedSize == 0 && originalSize > 0 && File.Exists(archivePath))
        {
            compressedSize = new FileInfo(archivePath).Length;
        }

        return new ArchiveSummary
        {
            Format = Path.GetExtension(archivePath).TrimStart('.').ToUpperInvariant(),
            FileCount = files.Count,
            FolderCount = entries.Count - files.Count,
            BlockCount = entries.Count,
            OriginalSize = originalSize,
            CompressedSize = compressedSize,
            Ratio = originalSize == 0 ? 1 : (double)compressedSize / originalSize,
            MethodsUsed = entries.Select(x => x.Method).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(),
            IsEncrypted = entries.Any(x => x.IsEncrypted),
            Notes = "External archive metadata is normalized; test validates readability and exposed checksums only."
        };
    }

    public async Task ExtractAsync(
        string archivePath,
        string destinationFolder,
        ExtractArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var totalBytes = List(archivePath, options.Password)
            .Where(x => !x.IsDirectory)
            .Sum(x => x.OriginalSize);
        long processedBytes = 0;
        Directory.CreateDirectory(destinationFolder);

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, CreateReaderOptions(archivePath, options.Password));
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(entry.LinkTarget))
                {
                    throw new InvalidDataException($"Archive links are not extracted for safety: {entry.Key}");
                }

                if (entry.IsEncrypted && options.Password is null)
                {
                    throw new ArchivePasswordRequiredException(archivePath);
                }

                var entryPath = NormalizeEntryName(entry.Key, archivePath);
                var outPath = PathSecurity.EnsureSafeExtractionPath(destinationFolder, entryPath);
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

                await using var input = entry.OpenEntryStream();
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
                        CurrentItem = entryPath,
                        ProcessedBytes = processedBytes,
                        TotalBytes = totalBytes,
                        Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
                    });
                }
            }
        }
        catch (Exception ex) when (options.Password is null && IsPasswordLikeFailure(ex))
        {
            throw new ArchivePasswordRequiredException(archivePath);
        }
        catch (Exception ex) when (options.Password is not null && IsPasswordLikeFailure(ex))
        {
            throw new ArchivePasswordException("Invalid password or corrupted encrypted archive.");
        }
    }

    public async Task<ArchiveTestResult> TestAsync(string archivePath, PasswordContext? password = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, CreateReaderOptions(archivePath, password));
            var files = 0;
            var entries = 0;
            var buffer = new byte[128 * 1024];
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries++;
                if (entry.IsDirectory)
                {
                    continue;
                }

                if (entry.IsEncrypted && password is null)
                {
                    return ArchiveTestResult.Failed($"Archive requires a password: {archivePath}");
                }

                await using var input = entry.OpenEntryStream();
                while (await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
                {
                }

                files++;
            }

            return ArchiveTestResult.Ok(files, entries, "External archive readability OK.");
        }
        catch (Exception ex) when (password is null && IsPasswordLikeFailure(ex))
        {
            return ArchiveTestResult.Failed($"Archive requires a password: {archivePath}");
        }
        catch (Exception ex) when (password is not null && IsPasswordLikeFailure(ex))
        {
            return ArchiveTestResult.Failed("Invalid password or corrupted encrypted archive.");
        }
        catch (Exception ex) when (IsUnsupportedArchiveFailure(ex))
        {
            return ArchiveTestResult.Failed($"Unsupported or unreadable archive: {ex.Message}");
        }
    }

    private static ReaderOptions CreateReaderOptions(string archivePath, PasswordContext? password)
    {
        return new ReaderOptions
        {
            Password = password?.Password,
            LookForHeader = true,
            ExtensionHint = Path.GetExtension(archivePath)
        };
    }

    private static string NormalizeEntryName(string? key, string archivePath)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key;
        }

        var name = Path.GetFileNameWithoutExtension(archivePath);
        return string.IsNullOrWhiteSpace(name) ? "archive-entry" : name;
    }

    private static bool IsPasswordLikeFailure(Exception ex)
    {
        return ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("decrypt", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsUnsupportedArchiveFailure(Exception ex)
    {
        return ex is InvalidFormatException or IncompleteArchiveException or InvalidOperationException or InvalidDataException or NotSupportedException ||
               ex.Message.Contains("Cannot determine", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("No factory", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Failed to read TAR header", StringComparison.OrdinalIgnoreCase);
    }
}
