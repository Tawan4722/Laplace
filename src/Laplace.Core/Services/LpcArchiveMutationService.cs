using Laplace.Core.Abstractions;
using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using Laplace.Core.Security;
using System.Text.RegularExpressions;

namespace Laplace.Core.Services;

public sealed class LpcArchiveMutationService
{
    private readonly ICompressorRegistry _compressorRegistry;
    private readonly ArchiveReader _reader = new();

    public LpcArchiveMutationService(ICompressorRegistry compressorRegistry)
    {
        _compressorRegistry = compressorRegistry;
    }

    public Task AddAsync(string archivePath, IEnumerable<string> inputPaths, MutateArchiveOptions options, CancellationToken cancellationToken = default)
    {
        var paths = inputPaths.Select(Path.GetFullPath).ToArray();
        if (paths.Length == 0)
        {
            throw new ArgumentException("At least one input path is required.", nameof(inputPaths));
        }

        return RewriteAsync(
            archivePath,
            options,
            workspace =>
            {
                foreach (var input in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(input) && !Directory.Exists(input))
                    {
                        throw new FileNotFoundException($"Input path not found: {input}", input);
                    }

                    var destination = Path.Combine(workspace, Path.GetFileName(input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                    ReplacePath(input, destination);
                }
            },
            preserveLock: true,
            cancellationToken);
    }

    public Task FreshenAsync(string archivePath, IEnumerable<string> inputPaths, MutateArchiveOptions options, CancellationToken cancellationToken = default)
    {
        var paths = inputPaths.Select(Path.GetFullPath).ToArray();
        if (paths.Length == 0)
        {
            throw new ArgumentException("At least one input path is required.", nameof(inputPaths));
        }

        var archive = _reader.Read(archivePath, options.Password);
        var entriesByPath = archive.FileEntries.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        return RewriteAsync(
            archivePath,
            options,
            workspace =>
            {
                foreach (var input in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(input) && !Directory.Exists(input))
                    {
                        throw new FileNotFoundException($"Input path not found: {input}", input);
                    }

                    var destination = Path.Combine(workspace, Path.GetFileName(input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                    var relativeDestination = Path.GetRelativePath(workspace, destination).Replace('\\', '/');
                    if (!entriesByPath.TryGetValue(relativeDestination, out var existingEntry))
                    {
                        continue;
                    }

                    var archivedModified = DateTimeOffset.FromUnixTimeMilliseconds(existingEntry.ModifiedUnixMilliseconds).UtcDateTime;
                    if (GetLastWriteTimeUtc(input) > archivedModified)
                    {
                        ReplacePath(input, destination);
                    }
                }
            },
            preserveLock: true,
            cancellationToken,
            archive);
    }

    public Task DeleteAsync(string archivePath, IEnumerable<string> targets, MutateArchiveOptions options, CancellationToken cancellationToken = default)
    {
        var targetList = targets.ToArray();
        if (targetList.Length == 0)
        {
            throw new ArgumentException("At least one archive entry target is required.", nameof(targets));
        }

        var archive = _reader.Read(archivePath, options.Password);
        return RewriteAsync(
            archivePath,
            options,
            workspace =>
            {
                foreach (var relativePath in ResolveTargets(archive, targetList))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = PathSecurity.EnsureSafeExtractionPath(workspace, relativePath);
                    DeletePathIfExists(fullPath);
                }
            },
            preserveLock: true,
            cancellationToken,
            archive);
    }

    public Task RenameAsync(string archivePath, string target, string newRelativePath, MutateArchiveOptions options, CancellationToken cancellationToken = default)
    {
        var archive = _reader.Read(archivePath, options.Password);
        var oldRelativePath = ResolveSingleTarget(archive, target);
        return RewriteAsync(
            archivePath,
            options,
            workspace =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var oldPath = PathSecurity.EnsureSafeExtractionPath(workspace, oldRelativePath);
                var newPath = PathSecurity.EnsureSafeExtractionPath(workspace, newRelativePath);
                if (!File.Exists(oldPath) && !Directory.Exists(oldPath))
                {
                    throw new FileNotFoundException($"Archive entry not found after extraction: {oldRelativePath}");
                }

                var parent = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                DeletePathIfExists(newPath);
                if (Directory.Exists(oldPath))
                {
                    Directory.Move(oldPath, newPath);
                }
                else
                {
                    File.Move(oldPath, newPath);
                }
            },
            preserveLock: true,
            cancellationToken,
            archive);
    }

    public Task SetCommentAsync(string archivePath, string comment, MutateArchiveOptions options, CancellationToken cancellationToken = default)
    {
        return RewriteAsync(archivePath, options, _ => { }, preserveLock: true, cancellationToken, newComment: comment);
    }

    public Task ClearCommentAsync(string archivePath, MutateArchiveOptions options, CancellationToken cancellationToken = default)
    {
        return SetCommentAsync(archivePath, string.Empty, options, cancellationToken);
    }

    public Task LockAsync(string archivePath, MutateArchiveOptions options, CancellationToken cancellationToken = default)
    {
        return RewriteAsync(archivePath, options, _ => { }, preserveLock: false, cancellationToken, forceLock: true);
    }

    public IReadOnlyList<ArchiveFindResult> Find(string archivePath, ArchiveFindOptions options)
    {
        var archive = _reader.Read(archivePath, options.Password);
        var regex = GlobToRegex(string.IsNullOrWhiteSpace(options.NamePattern) ? "*" : options.NamePattern);
        var nameMatches = archive.FileEntries
            .Where(entry => regex.IsMatch(entry.RelativePath))
            .Select(entry => new ArchiveFindResult
            {
                Id = entry.EntryId,
                Path = entry.RelativePath,
                IsDirectory = entry.IsDirectory,
                OriginalSize = entry.OriginalSize,
                NameMatched = true
            })
            .ToList();

        if (string.IsNullOrEmpty(options.Text))
        {
            return nameMatches;
        }

        var tempRoot = CreateTempRoot();
        try
        {
            new ArchiveExtractor(_compressorRegistry, _reader).ExtractAsync(
                archivePath,
                tempRoot,
                new ExtractArchiveOptions { Overwrite = true, Password = options.Password },
                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

            var byPath = nameMatches.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.FileEntries.Where(x => !x.IsDirectory))
            {
                var filePath = PathSecurity.EnsureSafeExtractionPath(tempRoot, entry.RelativePath);
                if (!File.Exists(filePath))
                {
                    continue;
                }

                var textMatched = File.ReadLines(filePath).Any(line => line.Contains(options.Text, StringComparison.OrdinalIgnoreCase));
                if (!textMatched)
                {
                    continue;
                }

                if (byPath.TryGetValue(entry.RelativePath, out var existing))
                {
                    byPath[entry.RelativePath] = new ArchiveFindResult
                    {
                        Id = existing.Id,
                        Path = existing.Path,
                        IsDirectory = existing.IsDirectory,
                        OriginalSize = existing.OriginalSize,
                        NameMatched = existing.NameMatched,
                        TextMatched = true
                    };
                }
                else
                {
                    byPath[entry.RelativePath] = new ArchiveFindResult
                    {
                        Id = entry.EntryId,
                        Path = entry.RelativePath,
                        IsDirectory = false,
                        OriginalSize = entry.OriginalSize,
                        TextMatched = true
                    };
                }
            }

            return byPath.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public byte[] ViewFile(string archivePath, string target, PasswordContext? password)
    {
        var archive = _reader.Read(archivePath, password);
        var relativePath = ResolveSingleTarget(archive, target);
        var entry = archive.FileEntries.Single(x => string.Equals(x.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (entry.IsDirectory)
        {
            throw new InvalidOperationException("Cannot view a directory entry.");
        }

        var tempRoot = CreateTempRoot();
        try
        {
            new ArchiveExtractor(_compressorRegistry, _reader).ExtractAsync(
                archivePath,
                tempRoot,
                new ExtractArchiveOptions { Overwrite = true, Password = password, SelectedEntryIds = new HashSet<long> { entry.EntryId } },
                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            var filePath = PathSecurity.EnsureSafeExtractionPath(tempRoot, relativePath);
            return File.ReadAllBytes(filePath);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task RewriteAsync(
        string archivePath,
        MutateArchiveOptions options,
        Action<string> mutateWorkspace,
        bool preserveLock,
        CancellationToken cancellationToken,
        ArchiveDocument? archive = null,
        string? newComment = null,
        bool forceLock = false)
    {
        archive ??= _reader.Read(archivePath, options.Password);
        if (archive.Header.IsLocked && preserveLock)
        {
            throw new InvalidOperationException("Archive is locked and cannot be mutated.");
        }

        if (archive.Header.IsEncrypted && options.Password is null)
        {
            throw new ArchivePasswordRequiredException(archivePath);
        }

        var tempRoot = CreateTempRoot();
        var replacementPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archivePath))!, $".{Path.GetFileName(archivePath)}.{Guid.NewGuid():N}.tmp");
        var backupPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archivePath))!, $".{Path.GetFileName(archivePath)}.{Guid.NewGuid():N}.bak");
        try
        {
            var extractor = new ArchiveExtractor(_compressorRegistry, _reader);
            await extractor.ExtractAsync(
                archivePath,
                tempRoot,
                new ExtractArchiveOptions { Overwrite = true, Password = options.Password },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            mutateWorkspace(tempRoot);

            var inputs = Directory.EnumerateFileSystemEntries(tempRoot).ToArray();
            var writer = new ArchiveWriter(_compressorRegistry);
            await writer.CreateAsync(
                inputs,
                replacementPath,
                new CreateArchiveOptions
                {
                    Mode = options.Mode,
                    BlockSizeBytes = options.BlockSizeBytes ?? Math.Max(4 * 1024 * 1024, (int)archive.Header.DefaultBlockSize),
                    SolidMode = archive.Header.IsSolid ? Enums.SolidMode.On : Enums.SolidMode.Off,
                    Password = archive.Header.IsEncrypted ? options.Password : null,
                    KeyDerivationAlgorithm = archive.Header.FormatVersion >= 5
                        ? (Enums.KeyDerivationAlgorithm)archive.Header.KeyDerivationAlgorithmId
                        : Enums.KeyDerivationAlgorithm.Pbkdf2Sha256,
                    KeyDerivationIterations = archive.Header.FormatVersion < 5 ||
                                              archive.Header.KeyDerivationAlgorithmId == (byte)Enums.KeyDerivationAlgorithm.Pbkdf2Sha256
                        ? archive.Header.KeyDerivationIterations
                        : CreateArchiveOptions.DefaultKeyDerivationIterations,
                    Argon2Iterations = archive.Header.KeyDerivationAlgorithmId == (byte)Enums.KeyDerivationAlgorithm.Argon2id
                        ? archive.Header.KeyDerivationIterations
                        : CreateArchiveOptions.DefaultArgon2Iterations,
                    Argon2MemoryKiB = archive.Header.KeyDerivationMemoryKiB > 0
                        ? archive.Header.KeyDerivationMemoryKiB
                        : CreateArchiveOptions.DefaultArgon2MemoryKiB,
                    Argon2Parallelism = archive.Header.KeyDerivationParallelism > 0
                        ? archive.Header.KeyDerivationParallelism
                        : Math.Clamp(Environment.ProcessorCount, 1, 4),
                    EncryptMetadata = archive.Header.IsMetadataEncrypted,
                    RecoveryPercent = archive.Header.HasRecoveryRecord ? archive.Header.RecoveryPercent : 0,
                    Comment = newComment ?? archive.Header.Comment,
                    LockArchive = forceLock || archive.Header.IsLocked
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (options.VerifyAfterRewrite)
            {
                var test = await new ArchiveTester(_compressorRegistry, _reader)
                    .TestAsync(replacementPath, archive.Header.IsEncrypted ? options.Password : null, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (!test.Success)
                {
                    throw new InvalidDataException($"Rewritten archive failed verification: {test.Message}");
                }
            }

            File.Move(archivePath, backupPath);
            File.Move(replacementPath, archivePath);
            TryDelete(backupPath);
        }
        catch
        {
            if (File.Exists(backupPath) && !File.Exists(archivePath))
            {
                File.Move(backupPath, archivePath);
            }

            throw;
        }
        finally
        {
            TryDelete(replacementPath);
            TryDeleteDirectory(tempRoot);
        }
    }

    private static IEnumerable<string> ResolveTargets(ArchiveDocument archive, IEnumerable<string> targets)
    {
        foreach (var target in targets)
        {
            yield return ResolveSingleTarget(archive, target);
        }
    }

    private static string ResolveSingleTarget(ArchiveDocument archive, string target)
    {
        if (long.TryParse(target, out var id))
        {
            var byId = archive.FileEntries.SingleOrDefault(x => x.EntryId == id);
            if (byId is null)
            {
                throw new InvalidOperationException($"Archive entry id not found: {id}");
            }

            return byId.RelativePath;
        }

        var normalized = PathSecurity.NormalizeArchivePath(target);
        var byPath = archive.FileEntries.SingleOrDefault(x => string.Equals(x.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));
        if (byPath is null)
        {
            throw new InvalidOperationException($"Archive entry path not found: {target}");
        }

        return byPath.RelativePath;
    }

    private static void ReplacePath(string source, string destination)
    {
        DeletePathIfExists(destination);
        if (Directory.Exists(source))
        {
            CopyDirectory(source, destination);
        }
        else
        {
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Copy(source, destination, overwrite: true);
            File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source));
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            File.SetAttributes(destination, File.GetAttributes(source));
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        Directory.SetCreationTimeUtc(destination, Directory.GetCreationTimeUtc(source));
        Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(target);
            Directory.SetCreationTimeUtc(target, Directory.GetCreationTimeUtc(directory));
            Directory.SetLastWriteTimeUtc(target, Directory.GetLastWriteTimeUtc(directory));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(file));
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            File.SetAttributes(target, File.GetAttributes(file));
        }
    }

    private static void DeletePathIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static DateTime GetLastWriteTimeUtc(string path)
    {
        return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
    }

    private static Regex GlobToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-mut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
