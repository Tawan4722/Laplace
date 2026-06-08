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
        EnsureNoKeyfile(password);
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
                    Method = GetCompressionMethodName(entry),
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

    private static string GetCompressionMethodName(IArchiveEntry entry)
    {
        try
        {
            return entry.CompressionType.ToString();
        }
        catch
        {
            return "Unknown";
        }
    }

    public async Task ExtractAsync(
        string archivePath,
        string destinationFolder,
        ExtractArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        long processedBytes = 0;
        Directory.CreateDirectory(destinationFolder);
        EnsureNoKeyfile(options.Password);

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, CreateReaderOptions(archivePath, options.Password));
            var entries = archive.Entries
                .Select((entry, index) => new IndexedArchiveEntry(entry, index, NormalizeEntryName(entry.Key, archivePath)))
                .ToList();
            var selectedEntries = FilterSelectedEntries(entries, options.SelectedEntryIds).ToList();
            var totalBytes = selectedEntries
                .Where(x => !x.Entry.IsDirectory)
                .Sum(x => Math.Max(0, x.Entry.Size));
            foreach (var selectedEntry in selectedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = selectedEntry.Entry;
                if (!string.IsNullOrWhiteSpace(entry.LinkTarget))
                {
                    throw new InvalidDataException($"Archive links are not extracted for safety: {entry.Key}");
                }

                if (entry.IsEncrypted && options.Password is null)
                {
                    throw new ArchivePasswordRequiredException(archivePath);
                }

                var entryPath = selectedEntry.Path;
                var outPath = PathSecurity.EnsureSafeExtractionPath(destinationFolder, entryPath);
                if (entry.IsDirectory)
                {
                    PathSecurity.EnsureNoReparsePointInPath(destinationFolder, outPath);
                    Directory.CreateDirectory(outPath);
                    continue;
                }

                var parentDir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    PathSecurity.EnsureNoReparsePointInPath(destinationFolder, parentDir);
                    Directory.CreateDirectory(parentDir);
                }

                if (!options.Overwrite && File.Exists(outPath))
                {
                    throw new IOException($"File already exists: {outPath}. Use overwrite mode to replace.");
                }

                PathSecurity.EnsureNoReparsePointInPath(destinationFolder, outPath);
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
            EnsureNoKeyfile(password);
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

    private static void EnsureNoKeyfile(PasswordContext? password)
    {
        if (password?.HasKeyfile == true)
        {
            throw new NotSupportedException("Keyfiles are supported for LPC archives only.");
        }
    }

    internal static bool IsUnsupportedArchiveFailure(Exception ex)
    {
        return ex is InvalidFormatException or IncompleteArchiveException or InvalidOperationException or InvalidDataException or NotSupportedException ||
               ex.Message.Contains("Cannot determine", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("No factory", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("Failed to read TAR header", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<IndexedArchiveEntry> FilterSelectedEntries(
        IReadOnlyList<IndexedArchiveEntry> entries,
        IReadOnlySet<long>? selectedEntryIds)
    {
        if (selectedEntryIds is null || selectedEntryIds.Count == 0)
        {
            return entries;
        }

        var selectedDirectoryPrefixes = entries
            .Where(x => selectedEntryIds.Contains(x.Id) && x.Entry.IsDirectory)
            .Select(x => NormalizeDirectoryPrefix(x.Path))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        return entries.Where(x => selectedEntryIds.Contains(x.Id) ||
                                  selectedDirectoryPrefixes.Any(prefix => IsDescendant(x.Path, prefix)));
    }

    private static string NormalizeDirectoryPrefix(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : $"{normalized}/";
    }

    private static bool IsDescendant(string path, string directoryPrefix)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record IndexedArchiveEntry(IArchiveEntry Entry, long Id, string Path);
}
