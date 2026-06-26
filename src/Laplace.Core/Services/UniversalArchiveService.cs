using Laplace.Core.Abstractions;
using Laplace.Core.Models;

namespace Laplace.Core.Services;

public sealed class UniversalArchiveService
{
    private readonly ArchiveWriter _lpcWriter;
    private readonly ArchiveReader _lpcReader;
    private readonly ArchiveExtractor _lpcExtractor;
    private readonly ArchiveTester _lpcTester;
    private readonly ArchiveEstimator _estimator;
    private readonly ZipArchiveWriter _zipWriter = new();
    private readonly SevenZipArchiveWriter _sevenZipWriter = new();
    private readonly RarArchiveWriter _rarWriter = new();
    private readonly ZipArchiveHandler _zipHandler = new();
    private readonly SharpCompressArchiveHandler _externalHandler = new();
    private readonly WindowsNativeArchiveHandler _windowsNativeHandler = new();

    public UniversalArchiveService(ICompressorRegistry compressorRegistry)
    {
        _lpcWriter = new ArchiveWriter(compressorRegistry);
        _lpcReader = new ArchiveReader();
        _lpcExtractor = new ArchiveExtractor(compressorRegistry, _lpcReader);
        _lpcTester = new ArchiveTester(compressorRegistry, _lpcReader);
        _estimator = new ArchiveEstimator(compressorRegistry);
    }

    public async Task CompressAsync(
        IEnumerable<string> inputPaths,
        string outputArchivePath,
        CreateArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var writeKind = ArchiveFormatDetector.DetectWriteKind(outputArchivePath);
        if (options.Mode == Laplace.Core.Enums.CompressionMode.Extreme && writeKind != SupportedArchiveKind.Lpc)
        {
            throw new NotSupportedException("Extreme mode is supported for LPC archives only.");
        }
        if (options.Password?.HasKeyfile == true && writeKind != SupportedArchiveKind.Lpc)
        {
            throw new NotSupportedException("Keyfiles are supported for LPC archives only.");
        }
        if (writeKind != SupportedArchiveKind.Lpc && (options.EncryptMetadata || options.RecoveryPercent > 0))
        {
            throw new NotSupportedException("Metadata encryption and recovery records are supported for LPC archives only.");
        }
        if (options.VolumeSizeBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.VolumeSizeBytes), "Volume size must be greater than zero.");
        }
        if (writeKind == SupportedArchiveKind.Zip && options.VolumeSizeBytes is not null)
        {
            throw new NotSupportedException("Multi-volume ZIP creation is not supported. Use .7z or .rar output.");
        }

        switch (writeKind)
        {
            case SupportedArchiveKind.Lpc:
                await _lpcWriter.CreateAsync(inputPaths, outputArchivePath, options, progress, cancellationToken).ConfigureAwait(false);
                return;
            case SupportedArchiveKind.Zip:
                await _zipWriter.CreateAsync(inputPaths, outputArchivePath, options, progress, cancellationToken).ConfigureAwait(false);
                return;
            case SupportedArchiveKind.SevenZip:
                await _sevenZipWriter.CreateAsync(inputPaths, outputArchivePath, options, progress, cancellationToken).ConfigureAwait(false);
                return;
            case SupportedArchiveKind.Rar:
                await _rarWriter.CreateAsync(inputPaths, outputArchivePath, options, progress, cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new NotSupportedException("Unsupported write format.");
        }
    }

    public async Task<ExtractResult> ExtractAsync(
        string archivePath,
        string destinationFolder,
        ExtractArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        switch (ArchiveFormatDetector.DetectReadKind(archivePath))
        {
            case SupportedArchiveKind.Lpc:
                return await _lpcExtractor.ExtractAsync(archivePath, destinationFolder, options, progress, cancellationToken).ConfigureAwait(false);
            case SupportedArchiveKind.Zip:
                await _zipHandler.ExtractAsync(archivePath, destinationFolder, options, progress, cancellationToken).ConfigureAwait(false);
                return new ExtractResult();
            default:
                await ExtractExternalAsync(archivePath, destinationFolder, options, progress, cancellationToken).ConfigureAwait(false);
                return new ExtractResult();
        }
    }

    public IReadOnlyList<ArchiveEntryListing> List(string archivePath, PasswordContext? password = null)
    {
        return ArchiveFormatDetector.DetectReadKind(archivePath) switch
        {
            SupportedArchiveKind.Lpc => ListLpc(archivePath, password),
            SupportedArchiveKind.Zip => _zipHandler.List(archivePath, password),
            _ => _externalHandler.List(archivePath, password)
        };
    }

    public ArchiveSummary Info(string archivePath, PasswordContext? password = null)
    {
        return ArchiveFormatDetector.DetectReadKind(archivePath) switch
        {
            SupportedArchiveKind.Lpc => InfoLpc(archivePath, password),
            SupportedArchiveKind.Zip => _zipHandler.Info(archivePath, password),
            _ => _externalHandler.Info(archivePath, password)
        };
    }

    public async Task<ArchiveTestResult> TestAsync(string archivePath, PasswordContext? password = null, CancellationToken cancellationToken = default)
    {
        return ArchiveFormatDetector.DetectReadKind(archivePath) switch
        {
            SupportedArchiveKind.Lpc => ConvertLpcTest(await _lpcTester.TestAsync(archivePath, password, cancellationToken: cancellationToken).ConfigureAwait(false)),
            SupportedArchiveKind.Zip => await _zipHandler.TestAsync(archivePath, password, cancellationToken).ConfigureAwait(false),
            _ => await _externalHandler.TestAsync(archivePath, password, cancellationToken).ConfigureAwait(false)
        };
    }

    public bool IsEncrypted(string archivePath)
    {
        if (ArchiveFormatDetector.DetectReadKind(archivePath) == SupportedArchiveKind.Lpc)
        {
            return _lpcReader.ReadHeaderOnly(archivePath).IsEncrypted;
        }
        return Info(archivePath).IsEncrypted;
    }

    public Task<ArchiveEstimate> EstimateAsync(
        IEnumerable<string> inputPaths,
        CreateArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return _estimator.EstimateAsync(inputPaths, options, progress, cancellationToken);
    }

    private IReadOnlyList<ArchiveEntryListing> ListLpc(string archivePath, PasswordContext? password)
    {
        var archive = _lpcReader.Read(archivePath, password);
        return archive.FileEntries
            .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(entry =>
            {
                var method = "-";
                if (!entry.IsDirectory && entry.BlockCount > 0 && entry.FirstBlockIndex >= 0)
                {
                    var blocks = archive.BlockEntries
                        .Skip((int)entry.FirstBlockIndex)
                        .Take(entry.BlockCount);
                    method = string.Join(",", blocks.Select(b => b.CompressionMethod.ToString()).Distinct());
                }

                return new ArchiveEntryListing
                {
                    Id = entry.EntryId,
                    IsDirectory = entry.IsDirectory,
                    OriginalSize = entry.OriginalSize,
                    CompressedSize = entry.CompressedSize,
                    Method = method,
                    Path = entry.RelativePath,
                    IsEncrypted = archive.Header.IsEncrypted
                };
            })
            .ToList();
    }

    private async Task ExtractExternalAsync(
        string archivePath,
        string destinationFolder,
        ExtractArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _externalHandler.ExtractAsync(archivePath, destinationFolder, options, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_windowsNativeHandler.IsAvailable &&
                                   options.Password is null &&
                                   options.SelectedEntryIds is null &&
                                   SharpCompressArchiveHandler.IsUnsupportedArchiveFailure(ex))
        {
            await _windowsNativeHandler.ExtractAsync(archivePath, destinationFolder, options, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private ArchiveSummary InfoLpc(string archivePath, PasswordContext? password)
    {
        var archive = _lpcReader.Read(archivePath, password);
        var info = ArchiveInfoBuilder.Build(archive);
        return new ArchiveSummary
        {
            Format = "LPC",
            ArchiveVersion = info.ArchiveVersion,
            FileCount = info.FileCount,
            FolderCount = info.FolderCount,
            BlockCount = info.BlockCount,
            OriginalSize = info.OriginalSize,
            CompressedSize = info.CompressedSize,
            Ratio = info.Ratio,
            MethodsUsed = info.MethodsUsed,
            CreatedUtc = info.CreatedUtc,
            IsEncrypted = info.IsEncrypted,
            IsLocked = info.IsLocked,
            IsMetadataEncrypted = archive.Header.IsMetadataEncrypted,
            HasRecoveryRecord = archive.Header.HasRecoveryRecord,
            Comment = info.Comment,
            OptionalHeaderMetadataJson = info.OptionalHeaderMetadataJson,
            Notes = string.Join(" ", new[]
            {
                archive.Header.IsMetadataEncrypted
                    ? "LPC payload blocks and metadata tables are encrypted."
                    : info.IsEncrypted
                        ? "LPC payload blocks are encrypted; filenames and metadata remain visible."
                        : string.Empty,
                archive.Header.HasRecoveryRecord ? "LPC recovery records are available." : string.Empty
            }.Where(note => note.Length > 0))
        };

    }

    private static ArchiveTestResult ConvertLpcTest(TestArchiveResult result)
    {
        return result.Success
            ? ArchiveTestResult.Ok(result.FileCount, result.BlockCount, result.Message)
            : ArchiveTestResult.Failed(result.Message);
    }
}
