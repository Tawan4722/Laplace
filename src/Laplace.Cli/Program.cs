using Laplace.Compression;
using Laplace.Core.Abstractions;
using Laplace.Core.Enums;
using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using Laplace.Core.Services;
using Laplace.ShellIntegration;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Laplace.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            if (TryLaunchDesktop([]))
            {
                return 0;
            }

            PrintUsage();
            return 1;
        }

        if (args[0] is "--help" or "-h" or "/?")
        {
            PrintUsage();
            return 0;
        }

        var registry = new CompressorRegistry();
        var writer = new ArchiveWriter(registry);
        var reader = new ArchiveReader();
        var extractor = new ArchiveExtractor(registry, reader);
        var archives = new UniversalArchiveService(registry);
        var mutator = new LpcArchiveMutationService(registry);
        var rarTools = new RarToolCommandService();
        var recovery = new LpcRecoveryService();

        try
        {
            var command = args[0].ToLowerInvariant();
            var remaining = args.Skip(1).ToArray();
            return command switch
            {
                "compress" => await CompressAsync(archives, remaining).ConfigureAwait(false),
                "compress-beside" => await CompressBesideAsync(archives, remaining).ConfigureAwait(false),
                "estimate" => await EstimateAsync(archives, remaining).ConfigureAwait(false),
                "extract" => await ExtractAsync(archives, remaining).ConfigureAwait(false),
                "list" => await ListAsync(archives, remaining).ConfigureAwait(false),
                "info" => await InfoAsync(archives, remaining).ConfigureAwait(false),
                "test" => await TestAsync(archives, remaining).ConfigureAwait(false),
                "add" => await AddAsync(mutator, rarTools, remaining).ConfigureAwait(false),
                "freshen" => await FreshenAsync(mutator, rarTools, remaining).ConfigureAwait(false),
                "delete" => await DeleteAsync(mutator, archives, rarTools, remaining).ConfigureAwait(false),
                "rename" => await RenameAsync(mutator, remaining).ConfigureAwait(false),
                "comment" => await CommentAsync(mutator, rarTools, remaining).ConfigureAwait(false),
                "lock" => await LockAsync(mutator, rarTools, remaining).ConfigureAwait(false),
                "find" => await FindAsync(mutator, archives, remaining).ConfigureAwait(false),
                "diff" => await DiffAsync(archives, reader, remaining).ConfigureAwait(false),
                "merge" => await MergeAsync(archives, remaining).ConfigureAwait(false),
                "split" => await SplitAsync(archives, remaining).ConfigureAwait(false),
                "view" => await ViewAsync(mutator, remaining).ConfigureAwait(false),
                "repair" => await RepairAsync(rarTools, recovery, remaining).ConfigureAwait(false),
                "benchmark" => await BenchmarkAsync(writer, extractor, reader, remaining).ConfigureAwait(false),
                "open" => OpenCommand(remaining),
                "extract-here" => await ExtractHereAsync(archives, remaining).ConfigureAwait(false),
                "extract-to-folder" => await ExtractToFolderAsync(archives, remaining).ConfigureAwait(false),
                "extract-to-named-folder" => await ExtractToNamedFolderAsync(archives, remaining).ConfigureAwait(false),
                "extract-dialog" => ExtractDialogCommand(remaining),
                "iso-to-drive-dialog" => IsoToDriveDialogCommand(remaining),
                "compress-dialog" => CompressDialogCommand(remaining),
                "integrate" => IntegrateCommand(remaining),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"laplace error: {ex.Message}");
            if (Environment.GetEnvironmentVariable("LAPLACE_DEBUG_STACK") == "1")
            {
                Console.Error.WriteLine(ex);
            }

            return 2;
        }
    }

    private static async Task<int> CompressAsync(UniversalArchiveService archives, string[] args)
    {
        var fromFileInputs = ReadFromFileValues(args);
        var positional = new List<string>();
        var optionStart = args.Length;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                optionStart = i;
                break;
            }

            positional.Add(args[i]);
        }

        if (positional.Count == 0 && fromFileInputs.Count == 0)
        {
            Console.Error.WriteLine("Usage: laplace compress <input_path...> [output.lpc|output.zip|output.7z|output.rar] [options] [--encrypt|--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        string outputPath;
        string[] inputPaths;
        if (positional.Count == 0)
        {
            if (fromFileInputs.Count != 1)
            {
                Console.Error.WriteLine("Multiple input paths require an explicit output archive path ending in .lpc, .zip, .7z, or .rar.");
                return 1;
            }

            var inputPath = Path.GetFullPath(fromFileInputs[0]);
            if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input path not found: {inputPath}");
                return 1;
            }

            inputPaths = [inputPath];
            outputPath = ArchivePathHelper.ResolveBesideArchivePath(inputPath);
        }
        else if (fromFileInputs.Count > 0 && positional.Count == 1 && LooksLikeWriteArchivePath(positional[0]))
        {
            outputPath = positional[0];
            inputPaths = fromFileInputs.ToArray();
        }
        else if (positional.Count == 1)
        {
            var inputPath = Path.GetFullPath(positional[0]);
            if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input path not found: {inputPath}");
                return 1;
            }

            inputPaths = [inputPath];
            outputPath = ArchivePathHelper.ResolveBesideArchivePath(inputPath);
        }
        else
        {
            outputPath = positional[^1];
            inputPaths = positional.Take(positional.Count - 1).ToArray();
            if (File.Exists(outputPath) || Directory.Exists(outputPath))
            {
                var outputExtension = Path.GetExtension(outputPath);
                if (!IsSupportedWriteExtension(outputExtension))
                {
                    Console.Error.WriteLine("Multiple input paths require an explicit output archive path ending in .lpc, .zip, .7z, or .rar.");
                    return 1;
                }
            }
        }

        inputPaths = inputPaths
            .Concat(fromFileInputs)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var inputPath in inputPaths)
        {
            if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input path not found: {inputPath}");
                return 1;
            }
        }

        if (positional.Count == 1 && fromFileInputs.Count > 0 && !LooksLikeWriteArchivePath(positional[0]) && inputPaths.Length > 1)
        {
            Console.Error.WriteLine("Multiple input paths require an explicit output archive path ending in .lpc, .zip, .7z, or .rar.");
            return 1;
        }

        var optionArgs = args.Skip(optionStart).ToArray();
        var options = ParseCreateOptions(optionArgs);
        var quiet = IsQuiet(optionArgs);
        var json = IsJson(optionArgs);
        var dryRun = IsDryRun(optionArgs);
        var passwordOptions = ParsePasswordOptions(optionArgs);
        if (passwordOptions.EncryptRequested || passwordOptions.HasExplicitSecret || options.EncryptMetadata)
        {
            options.Password = await ResolvePasswordAsync(
                passwordOptions,
                new PasswordRequest(outputPath, "Create archive", IsWrite: true),
                requirePassword: true,
                confirmInteractivePassword: !passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        }

        var originalSize = GetInputSize(inputPaths);
        if (dryRun)
        {
            WriteCommandResult(
                json,
                "Compression dry run completed.",
                new
                {
                    command = "compress",
                    dryRun = true,
                    inputPaths,
                    outputPath,
                    originalSize,
                    options = DescribeCreateOptions(options)
                });
            return 0;
        }

        var stopwatch = Stopwatch.StartNew();
        if (!json)
        {
            Console.WriteLine($"Compressing {inputPaths.Length} input path(s) -> '{outputPath}'");
        }

        await archives.CompressAsync(inputPaths, outputPath, options, quiet || json ? null : ProgressToConsole()).ConfigureAwait(false);
        stopwatch.Stop();

        var compressedSize = new FileInfo(outputPath).Length;
        if (json)
        {
            WriteJson(new
            {
                command = "compress",
                dryRun = false,
                inputPaths,
                outputPath,
                originalSize,
                compressedSize,
                ratio = originalSize == 0 ? 1 : (double)compressedSize / originalSize,
                elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
                options = DescribeCreateOptions(options)
            });
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("Compression completed.");
            PrintSizeStats(originalSize, compressedSize, stopwatch.Elapsed);
        }

        if (options.VerifyAfterCompression)
        {
            var testResult = await archives.TestAsync(outputPath, options.Password).ConfigureAwait(false);
            if (!json)
            {
                Console.WriteLine(testResult.Success ? "Verification: OK" : $"Verification: FAILED ({testResult.Message})");
            }
        }

        return 0;
    }

    private static async Task<int> CompressBesideAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace compress-beside <input_path> [options] [--encrypt|--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        var inputPath = Path.GetFullPath(args[0]);
        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input path not found: {inputPath}");
            return 1;
        }

        var optionArgs = args.Skip(1).ToArray();
        var outputPath = ArchivePathHelper.ResolveBesideArchivePath(inputPath);
        var options = ParseCreateOptions(optionArgs);
        var quiet = IsQuiet(optionArgs);
        var json = IsJson(optionArgs);
        var dryRun = IsDryRun(optionArgs);
        var passwordOptions = ParsePasswordOptions(optionArgs);
        if (passwordOptions.EncryptRequested || passwordOptions.HasExplicitSecret || options.EncryptMetadata)
        {
            options.Password = await ResolvePasswordAsync(
                passwordOptions,
                new PasswordRequest(outputPath, "Create archive", IsWrite: true),
                requirePassword: true,
                confirmInteractivePassword: !passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        }
        ValidatePasswordContextForArchiveWrite(outputPath, options.Password);

        var originalSize = GetInputSize([inputPath]);
        if (dryRun)
        {
            WriteCommandResult(
                json,
                "Compression dry run completed.",
                new
                {
                    command = "compress-beside",
                    dryRun = true,
                    inputPaths = new[] { inputPath },
                    outputPath,
                    originalSize,
                    options = DescribeCreateOptions(options)
                });
            return 0;
        }

        var stopwatch = Stopwatch.StartNew();
        if (!json)
        {
            Console.WriteLine($"Compressing '{inputPath}' -> '{outputPath}'");
        }

        await archives.CompressAsync([inputPath], outputPath, options, quiet || json ? null : ProgressToConsole()).ConfigureAwait(false);
        stopwatch.Stop();

        var compressedSize = new FileInfo(outputPath).Length;
        if (json)
        {
            WriteJson(new
            {
                command = "compress-beside",
                dryRun = false,
                inputPaths = new[] { inputPath },
                outputPath,
                originalSize,
                compressedSize,
                ratio = originalSize == 0 ? 1 : (double)compressedSize / originalSize,
                elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
                options = DescribeCreateOptions(options)
            });
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("Compression completed.");
            PrintSizeStats(originalSize, compressedSize, stopwatch.Elapsed);
        }

        if (options.VerifyAfterCompression)
        {
            var testResult = await archives.TestAsync(outputPath, options.Password).ConfigureAwait(false);
            if (!json)
            {
                Console.WriteLine(testResult.Success ? "Verification: OK" : $"Verification: FAILED ({testResult.Message})");
            }
        }

        return 0;
    }

    private static async Task<int> EstimateAsync(UniversalArchiveService archives, string[] args)
    {
        var positional = new List<string>();
        var optionStart = args.Length;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                optionStart = i;
                break;
            }

            positional.Add(args[i]);
        }

        if (positional.Count < 1)
        {
            Console.Error.WriteLine("Usage: laplace estimate <input_path...> [--mode fast|balanced|maximum|intensive|compressed|auto] [--block-size 8M] [--solid on|off|auto] [--threads N]");
            return 1;
        }

        var optionArgs = args.Skip(optionStart).ToArray();
        var options = ParseCreateOptions(optionArgs);
        var json = IsJson(optionArgs);
        if (!json)
        {
            Console.WriteLine($"Estimating {positional.Count} input path(s)...");
        }

        var estimate = await archives.EstimateAsync(positional, options).ConfigureAwait(false);
        if (json)
        {
            WriteJson(new
            {
                command = "estimate",
                inputPaths = positional,
                estimate
            });
        }
        else
        {
            PrintArchiveEstimate(estimate);
        }

        return 0;
    }

    private static async Task<int> ExtractAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: laplace extract <input_archive> <output_folder> [--overwrite] [--verify|--no-verify] [--quiet] [--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        var inputArchive = args[0];
        var outputFolder = args[1];
        var overwrite = args.Any(x => x.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var verifyChecksums = ParseVerifyChecksums(args);
        var quiet = IsQuiet(args);
        var json = IsJson(args);
        var dryRun = IsDryRun(args);
        var namePatterns = ReadPatternValues(args, "--name");
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(inputArchive, "Extract archive", IsWrite: false),
            requirePassword: passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(inputArchive, password);
        var selectedEntryIds = await ResolveSelectedEntryIdsAsync(archives, inputArchive, passwordOptions, password, namePatterns).ConfigureAwait(false);
        if (dryRun)
        {
            WriteCommandResult(
                json,
                "Extraction dry run completed.",
                new
                {
                    command = "extract",
                    dryRun = true,
                    inputArchive,
                    outputFolder,
                    namePatterns,
                    selectedEntryCount = selectedEntryIds?.Count,
                    overwrite,
                    verifyChecksums
                });
            return 0;
        }

        var stopwatch = Stopwatch.StartNew();
        if (!json)
        {
            Console.WriteLine($"Extracting '{inputArchive}' -> '{outputFolder}'");
        }

        await RunWithPasswordRetryAsync(
            inputArchive,
            "Extract archive",
            passwordOptions,
            password,
            async resolvedPassword => await archives.ExtractAsync(
                inputArchive,
                outputFolder,
                new ExtractArchiveOptions
                {
                    Overwrite = overwrite,
                    VerifyChecksums = verifyChecksums,
                    SelectedEntryIds = resolvedPassword == password ? selectedEntryIds : await ResolveSelectedEntryIdsAsync(archives, inputArchive, passwordOptions, resolvedPassword, namePatterns).ConfigureAwait(false),
                    Password = resolvedPassword
                },
                quiet || json ? null : ProgressToConsole()).ConfigureAwait(false)).ConfigureAwait(false);

        stopwatch.Stop();
        if (json)
        {
            WriteJson(new
            {
                command = "extract",
                dryRun = false,
                inputArchive,
                outputFolder,
                namePatterns,
                selectedEntryCount = selectedEntryIds?.Count,
                overwrite,
                verifyChecksums,
                elapsedSeconds = stopwatch.Elapsed.TotalSeconds
            });
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"Extraction completed in {stopwatch.Elapsed.TotalSeconds:F2}s.");
        }

        return 0;
    }

    private static async Task<int> ListAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace list <input_archive> [--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        var inputArchive = args[0];
        var json = IsJson(args);
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(inputArchive, "List archive", IsWrite: false),
            requirePassword: passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(inputArchive, password);

        var entries = await RunWithPasswordRetryAsync(
            inputArchive,
            "List archive",
            passwordOptions,
            password,
                resolvedPassword => Task.FromResult(archives.List(inputArchive, resolvedPassword))).ConfigureAwait(false);

        if (json)
        {
            WriteJson(new
            {
                command = "list",
                archive = inputArchive,
                entries
            });
        }
        else
        {
            Console.WriteLine($"Archive: {inputArchive}");
            Console.WriteLine("ID\tType\tOriginal\tCompressed\tMethod\tEncrypted\tPath");
            foreach (var entry in entries)
            {
                var type = entry.IsDirectory ? "DIR" : "FILE";
                Console.WriteLine($"{entry.Id}\t{type}\t{entry.OriginalSize}\t{entry.CompressedSize}\t{entry.Method}\t{entry.IsEncrypted}\t{entry.Path}");
            }
        }

        return 0;
    }

    private static async Task<int> InfoAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace info <input_archive> [--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        var inputArchive = args[0];
        var json = IsJson(args);
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(inputArchive, "Read archive info", IsWrite: false),
            requirePassword: passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(inputArchive, password);

        var info = await RunWithPasswordRetryAsync(
            inputArchive,
            "Read archive info",
            passwordOptions,
            password,
                resolvedPassword => Task.FromResult(archives.Info(inputArchive, resolvedPassword))).ConfigureAwait(false);

        if (json)
        {
            WriteJson(new
            {
                command = "info",
                archive = inputArchive,
                info
            });
        }
        else
        {
            PrintArchiveSummary(inputArchive, info);
        }

        return 0;
    }

    private static async Task<int> TestAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace test <input_archive> [--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        var inputArchive = args[0];
        var json = IsJson(args);
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(inputArchive, "Test archive", IsWrite: false),
            requirePassword: passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(inputArchive, password);

        if (!json)
        {
            Console.WriteLine($"Testing archive: {inputArchive}");
        }

        var result = await RunWithPasswordRetryAsync(
            inputArchive,
            "Test archive",
            passwordOptions,
            password,
            async resolvedPassword =>
            {
                var testResult = await archives.TestAsync(inputArchive, resolvedPassword).ConfigureAwait(false);
                if (!testResult.Success && IsPasswordRequiredMessage(testResult.Message) && resolvedPassword is null)
                {
                    throw new ArchivePasswordRequiredException(inputArchive);
                }

                return testResult;
            }).ConfigureAwait(false);

        if (json)
        {
            WriteJson(new
            {
                command = "test",
                archive = inputArchive,
                result
            });
        }
        else
        {
            Console.WriteLine();
        }

        if (result.Success)
        {
            if (!json)
            {
                Console.WriteLine($"Integrity OK. Files: {result.FileCount}, Blocks: {result.BlockCount}");
            }

            return 0;
        }

        if (!json)
        {
            Console.Error.WriteLine($"Integrity FAILED: {result.Message}");
        }

        return 2;
    }

    private static async Task<int> AddAsync(LpcArchiveMutationService mutator, RarToolCommandService rarTools, string[] args)
    {
        var (archivePath, inputs, options, passwordOptions) = await ParseMutationCommandAsync(args, "laplace add <archive> <input_path...> [options]").ConfigureAwait(false);
        var json = IsJson(args);
        var dryRun = IsDryRun(args);
        if (dryRun)
        {
            WriteCommandResult(json, "Add dry run completed.", DescribeMutation("add", archivePath, inputs, options, dryRun));
            return 0;
        }

        if (IsRarArchive(archivePath))
        {
            await rarTools.AddAsync(archivePath, inputs).ConfigureAwait(false);
            WriteCommandResult(json, "RAR archive updated.", DescribeMutation("add", archivePath, inputs, options, dryRun));
            return 0;
        }

        EnsureLpcArchive(archivePath);
        await RunWithPasswordRetryAsync(
            archivePath,
            "Update archive",
            passwordOptions,
            options.Password,
            async password =>
            {
                options.Password = password;
                await mutator.AddAsync(archivePath, inputs, options).ConfigureAwait(false);
            }).ConfigureAwait(false);
        WriteCommandResult(json, "Archive updated.", DescribeMutation("add", archivePath, inputs, options, dryRun));
        return 0;
    }

    private static async Task<int> FreshenAsync(LpcArchiveMutationService mutator, RarToolCommandService rarTools, string[] args)
    {
        var (archivePath, inputs, options, passwordOptions) = await ParseMutationCommandAsync(args, "laplace freshen <archive> <input_path...> [options]").ConfigureAwait(false);
        var json = IsJson(args);
        var dryRun = IsDryRun(args);
        if (dryRun)
        {
            WriteCommandResult(json, "Freshen dry run completed.", DescribeMutation("freshen", archivePath, inputs, options, dryRun));
            return 0;
        }

        if (IsRarArchive(archivePath))
        {
            await rarTools.FreshenAsync(archivePath, inputs).ConfigureAwait(false);
            WriteCommandResult(json, "RAR archive freshened.", DescribeMutation("freshen", archivePath, inputs, options, dryRun));
            return 0;
        }

        EnsureLpcArchive(archivePath);
        await RunWithPasswordRetryAsync(
            archivePath,
            "Freshen archive",
            passwordOptions,
            options.Password,
            async password =>
            {
                options.Password = password;
                await mutator.FreshenAsync(archivePath, inputs, options).ConfigureAwait(false);
            }).ConfigureAwait(false);
        WriteCommandResult(json, "Archive freshened.", DescribeMutation("freshen", archivePath, inputs, options, dryRun));
        return 0;
    }

    private static async Task<int> DeleteAsync(LpcArchiveMutationService mutator, UniversalArchiveService archives, RarToolCommandService rarTools, string[] args)
    {
        var (archivePath, rawTargets, options, passwordOptions) = await ParseMutationCommandAsync(args, "laplace delete <archive> <entry_path_or_id...> [options]").ConfigureAwait(false);
        var json = IsJson(args);
        var dryRun = IsDryRun(args);
        var password = options.Password;
        var targets = IsLpcArchive(archivePath)
            ? await ExpandDeleteTargetsAsync(archives, archivePath, rawTargets, passwordOptions, password).ConfigureAwait(false)
            : rawTargets;
        if (dryRun)
        {
            WriteCommandResult(json, "Delete dry run completed.", DescribeMutation("delete", archivePath, targets, options, dryRun));
            return 0;
        }

        if (IsRarArchive(archivePath))
        {
            await rarTools.DeleteAsync(archivePath, targets).ConfigureAwait(false);
            WriteCommandResult(json, "RAR entries deleted.", DescribeMutation("delete", archivePath, targets, options, dryRun));
            return 0;
        }

        EnsureLpcArchive(archivePath);
        await RunWithPasswordRetryAsync(
            archivePath,
            "Delete archive entries",
            passwordOptions,
            options.Password,
            async password =>
            {
                options.Password = password;
                await mutator.DeleteAsync(archivePath, targets, options).ConfigureAwait(false);
            }).ConfigureAwait(false);
        WriteCommandResult(json, "Archive entries deleted.", DescribeMutation("delete", archivePath, targets, options, dryRun));
        return 0;
    }

    private static async Task<int> RenameAsync(LpcArchiveMutationService mutator, string[] args)
    {
        var positional = GetPositionalArgs(args);
        if (positional.Count < 3)
        {
            Console.Error.WriteLine("Usage: laplace rename <archive.lpc> <entry_path_or_id> <new_entry_path> [options]");
            return 1;
        }

        var archivePath = positional[0];
        EnsureLpcArchive(archivePath);
        var passwordOptions = ParsePasswordOptions(args);
        var options = await ParseMutationOptionsAsync(archivePath, args, passwordOptions).ConfigureAwait(false);
        var json = IsJson(args);
        var dryRun = IsDryRun(args);
        if (dryRun)
        {
            WriteCommandResult(
                json,
                "Rename dry run completed.",
                new
                {
                    command = "rename",
                    dryRun = true,
                    archivePath,
                    target = positional[1],
                    newEntryPath = positional[2],
                    options = DescribeMutationOptions(options)
                });
            return 0;
        }

        await RunWithPasswordRetryAsync(
            archivePath,
            "Rename archive entry",
            passwordOptions,
            options.Password,
            async password =>
            {
                options.Password = password;
                await mutator.RenameAsync(archivePath, positional[1], positional[2], options).ConfigureAwait(false);
            }).ConfigureAwait(false);
        WriteCommandResult(
            json,
            "Archive entry renamed.",
            new
            {
                command = "rename",
                dryRun = false,
                archivePath,
                target = positional[1],
                newEntryPath = positional[2],
                options = DescribeMutationOptions(options)
            });
        return 0;
    }

    private static async Task<int> CommentAsync(LpcArchiveMutationService mutator, RarToolCommandService rarTools, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: laplace comment <archive> --show|--set <text>|--file <path>|--clear [options]");
            return 1;
        }

        var archivePath = args[0];
        var json = IsJson(args);
        var dryRun = IsDryRun(args);
        var passwordOptions = ParsePasswordOptions(args);
        var options = await ParseMutationOptionsAsync(archivePath, args, passwordOptions).ConfigureAwait(false);

        if (args.Any(x => x.Equals("--show", StringComparison.OrdinalIgnoreCase)))
        {
            if (!IsLpcArchive(archivePath))
            {
                Console.Error.WriteLine("Showing comments is currently supported for LPC archives only.");
                return 1;
            }

            var existingComment = new ArchiveReader().ReadHeaderOnly(archivePath).Comment;
            if (json)
            {
                WriteJson(new
                {
                    command = "comment",
                    archivePath,
                    action = "show",
                    comment = existingComment
                });
            }
            else
            {
                Console.WriteLine(existingComment);
            }

            return 0;
        }

        string? comment = null;
        var clear = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--set", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                comment = args[++i];
            }
            else if (args[i].Equals("--file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                comment = await File.ReadAllTextAsync(args[++i]).ConfigureAwait(false);
            }
            else if (args[i].Equals("--clear", StringComparison.OrdinalIgnoreCase))
            {
                clear = true;
                comment = string.Empty;
            }
        }

        if (comment is null)
        {
            Console.Error.WriteLine("Usage: laplace comment <archive> --show|--set <text>|--file <path>|--clear [options]");
            return 1;
        }

        if (dryRun)
        {
            WriteCommandResult(
                json,
                "Comment dry run completed.",
                new
                {
                    command = "comment",
                    dryRun = true,
                    archivePath,
                    action = clear ? "clear" : "set",
                    commentLength = comment.Length,
                    options = DescribeMutationOptions(options)
                });
            return 0;
        }

        if (IsRarArchive(archivePath))
        {
            await rarTools.SetCommentAsync(archivePath, comment).ConfigureAwait(false);
            WriteCommandResult(
                json,
                clear ? "RAR archive comment cleared." : "RAR archive comment updated.",
                new
                {
                    command = "comment",
                    dryRun = false,
                    archivePath,
                    action = clear ? "clear" : "set",
                    commentLength = comment.Length,
                    options = DescribeMutationOptions(options)
                });
            return 0;
        }

        EnsureLpcArchive(archivePath);
        await RunWithPasswordRetryAsync(
            archivePath,
            clear ? "Clear archive comment" : "Set archive comment",
            passwordOptions,
            options.Password,
            async password =>
            {
                options.Password = password;
                if (clear)
                {
                    await mutator.ClearCommentAsync(archivePath, options).ConfigureAwait(false);
                }
                else
                {
                    await mutator.SetCommentAsync(archivePath, comment, options).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        WriteCommandResult(
            json,
            clear ? "Archive comment cleared." : "Archive comment updated.",
            new
            {
                command = "comment",
                dryRun = false,
                archivePath,
                action = clear ? "clear" : "set",
                commentLength = comment.Length,
                options = DescribeMutationOptions(options)
            });
        return 0;
    }

    private static async Task<int> LockAsync(LpcArchiveMutationService mutator, RarToolCommandService rarTools, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace lock <archive> [--password <value>|--password-file <path>]");
            return 1;
        }

        var archivePath = args[0];
        var json = IsJson(args);
        var dryRun = IsDryRun(args);
        if (dryRun)
        {
            WriteCommandResult(json, "Lock dry run completed.", new { command = "lock", dryRun = true, archivePath });
            return 0;
        }

        if (IsRarArchive(archivePath))
        {
            await rarTools.LockAsync(archivePath).ConfigureAwait(false);
            WriteCommandResult(json, "RAR archive locked.", new { command = "lock", dryRun = false, archivePath });
            return 0;
        }

        EnsureLpcArchive(archivePath);
        var passwordOptions = ParsePasswordOptions(args);
        var options = await ParseMutationOptionsAsync(archivePath, args, passwordOptions).ConfigureAwait(false);
        await RunWithPasswordRetryAsync(
            archivePath,
            "Lock archive",
            passwordOptions,
            options.Password,
            async password =>
            {
                options.Password = password;
                await mutator.LockAsync(archivePath, options).ConfigureAwait(false);
            }).ConfigureAwait(false);
        WriteCommandResult(json, "Archive locked.", new { command = "lock", dryRun = false, archivePath, options = DescribeMutationOptions(options) });
        return 0;
    }

    private static async Task<int> FindAsync(LpcArchiveMutationService mutator, UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace find <archive> [--name <glob>] [--text <value>] [--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        var archivePath = args[0];
        var json = IsJson(args);
        var name = "*";
        string? text = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                name = args[++i];
            }
            else if (args[i].Equals("--text", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                text = args[++i];
            }
        }

        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(passwordOptions, new PasswordRequest(archivePath, "Find archive entries", IsWrite: false), passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(archivePath, password);
        if (!IsLpcArchive(archivePath))
        {
            if (!string.IsNullOrEmpty(text))
            {
                Console.Error.WriteLine("Text search is currently supported for LPC archives only.");
                return 1;
            }

            var entries = archives.List(archivePath, password).Where(x => MatchesSimpleGlob(x.Path, name)).ToArray();
            if (json)
            {
                WriteJson(new
                {
                    command = "find",
                    archive = archivePath,
                    namePattern = name,
                    text,
                    results = entries.Select(entry => new ArchiveFindResult
                    {
                        Id = entry.Id,
                        IsDirectory = entry.IsDirectory,
                        Path = entry.Path,
                        OriginalSize = entry.OriginalSize,
                        NameMatched = true
                    })
                });
            }
            else
            {
                foreach (var entry in entries)
                {
                    Console.WriteLine($"{entry.Id}\t{(entry.IsDirectory ? "DIR" : "FILE")}\t{entry.Path}");
                }
            }

            return 0;
        }

        var results = await RunWithPasswordRetryAsync(
            archivePath,
            "Find archive entries",
            passwordOptions,
            password,
            resolvedPassword => Task.FromResult(mutator.Find(archivePath, new ArchiveFindOptions
            {
                NamePattern = name,
                Text = text,
                Password = resolvedPassword
            }))).ConfigureAwait(false);
        if (json)
        {
            WriteJson(new
            {
                command = "find",
                archive = archivePath,
                namePattern = name,
                text,
                results
            });
        }
        else
        {
            foreach (var result in results)
            {
                var matched = result.TextMatched ? "name/text" : "name";
                Console.WriteLine($"{result.Id}\t{(result.IsDirectory ? "DIR" : "FILE")}\t{matched}\t{result.Path}");
            }
        }

        return 0;
    }

    private static async Task<int> DiffAsync(UniversalArchiveService archives, ArchiveReader reader, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: laplace diff <archive_a> <archive_b> [--json] [--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        var leftPath = args[0];
        var rightPath = args[1];
        var json = IsJson(args);
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(leftPath, "Diff archives", IsWrite: false),
            requirePassword: passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(leftPath, password);
        ValidatePasswordContextForArchiveRead(rightPath, password);

        var leftEntries = await RunWithPasswordRetryAsync(
            leftPath,
            "Diff archive",
            passwordOptions,
            password,
            resolvedPassword => Task.FromResult(archives.List(leftPath, resolvedPassword))).ConfigureAwait(false);
        var rightEntries = await RunWithPasswordRetryAsync(
            rightPath,
            "Diff archive",
            passwordOptions,
            password,
            resolvedPassword => Task.FromResult(archives.List(rightPath, resolvedPassword))).ConfigureAwait(false);

        var leftHashes = TryReadLpcChecksums(reader, leftPath, password);
        var rightHashes = TryReadLpcChecksums(reader, rightPath, password);
        var leftByPath = leftEntries.ToDictionary(x => NormalizeArchiveListingPath(x.Path), StringComparer.OrdinalIgnoreCase);
        var rightByPath = rightEntries.ToDictionary(x => NormalizeArchiveListingPath(x.Path), StringComparer.OrdinalIgnoreCase);
        var diff = new List<ArchiveDiffItem>();

        foreach (var path in leftByPath.Keys.Union(rightByPath.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var hasLeft = leftByPath.TryGetValue(path, out var left);
            var hasRight = rightByPath.TryGetValue(path, out var right);
            if (!hasLeft)
            {
                diff.Add(new ArchiveDiffItem(path, "added", right!.IsDirectory, null, right.OriginalSize, "present only in right archive"));
                continue;
            }

            if (!hasRight)
            {
                diff.Add(new ArchiveDiffItem(path, "removed", left!.IsDirectory, left.OriginalSize, null, "present only in left archive"));
                continue;
            }

            var leftEntry = left!;
            var rightEntry = right!;
            var reason = GetDiffReason(path, leftEntry, rightEntry, leftHashes, rightHashes);
            if (reason is not null)
            {
                diff.Add(new ArchiveDiffItem(path, "changed", leftEntry.IsDirectory || rightEntry.IsDirectory, leftEntry.OriginalSize, rightEntry.OriginalSize, reason));
            }
        }

        if (json)
        {
            WriteJson(new
            {
                command = "diff",
                leftArchive = leftPath,
                rightArchive = rightPath,
                changes = diff,
                summary = new
                {
                    added = diff.Count(x => x.Status == "added"),
                    removed = diff.Count(x => x.Status == "removed"),
                    changed = diff.Count(x => x.Status == "changed")
                }
            });
        }
        else
        {
            Console.WriteLine($"Diff: {leftPath}");
            Console.WriteLine($"   vs {rightPath}");
            foreach (var item in diff)
            {
                var prefix = item.Status switch
                {
                    "added" => "+",
                    "removed" => "-",
                    _ => "~"
                };
                Console.WriteLine($"{prefix} {item.Path} ({item.Reason})");
            }

            if (diff.Count == 0)
            {
                Console.WriteLine("No differences found.");
            }
        }

        return 0;
    }

    private static async Task<int> MergeAsync(UniversalArchiveService archives, string[] args)
    {
        var positional = GetPositionalArgs(args);
        var sourceArchives = positional.Count > 1
            ? positional.Skip(1).Concat(ReadFromFileValues(args)).ToArray()
            : ReadFromFileValues(args).ToArray();
        if (positional.Count < 2 && sourceArchives.Length == 0)
        {
            Console.Error.WriteLine("Usage: laplace merge <output_archive> <input_archive...> [--from-file <path>] [options]");
            return 1;
        }

        var outputArchive = positional[0];
        if (!LooksLikeWriteArchivePath(outputArchive))
        {
            Console.Error.WriteLine("Merge output path must end in .lpc, .zip, .7z, or .rar.");
            return 1;
        }

        sourceArchives = sourceArchives.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sourceArchives.Length == 0)
        {
            Console.Error.WriteLine("Merge requires at least one source archive.");
            return 1;
        }

        foreach (var archive in sourceArchives)
        {
            if (!File.Exists(archive))
            {
                Console.Error.WriteLine($"Source archive not found: {archive}");
                return 1;
            }
        }

        var json = IsJson(args);
        var dryRun = IsDryRun(args);
        var quiet = IsQuiet(args);
        var options = ParseCreateOptions(args);
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(outputArchive, "Merge archives", IsWrite: true),
            requirePassword: passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        if (passwordOptions.EncryptRequested || passwordOptions.HasExplicitSecret)
        {
            options.Password = password;
        }
        ValidatePasswordContextForArchiveWrite(outputArchive, options.Password);

        if (dryRun)
        {
            WriteCommandResult(
                json,
                "Merge dry run completed.",
                new
                {
                    command = "merge",
                    dryRun = true,
                    outputArchive,
                    sourceArchives,
                    options = DescribeCreateOptions(options)
                });
            return 0;
        }

        var workspace = CreateTempDirectory("laplace-merge");
        try
        {
            foreach (var archive in sourceArchives)
            {
                await RunWithPasswordRetryAsync(
                    archive,
                    "Merge archive",
                    passwordOptions,
                    password,
                    async activePassword => await archives.ExtractAsync(
                        archive,
                        workspace,
                        new ExtractArchiveOptions
                        {
                            Overwrite = true,
                            VerifyChecksums = true,
                            Password = activePassword
                        },
                        quiet || json ? null : ProgressToConsole()).ConfigureAwait(false)).ConfigureAwait(false);
            }

            var inputs = Directory.EnumerateFileSystemEntries(workspace).ToArray();
            if (inputs.Length == 0)
            {
                throw new InvalidOperationException("Merged archive would be empty.");
            }

            await archives.CompressAsync(inputs, outputArchive, options, quiet || json ? null : ProgressToConsole()).ConfigureAwait(false);
            WriteCommandResult(
                json,
                "Archive merge completed.",
                new
                {
                    command = "merge",
                    dryRun = false,
                    outputArchive,
                    sourceArchives,
                    options = DescribeCreateOptions(options)
                });
            return 0;
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }
    }

    private static async Task<int> SplitAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: laplace split <archive> <output_prefix> (--size 700M|--count 100) [--json] [--dry-run] [options]");
            return 1;
        }

        var archivePath = args[0];
        var outputPrefix = args[1];
        var sizeText = GetSingleOptionValue(args, "--size");
        var countText = GetSingleOptionValue(args, "--count");
        if (string.IsNullOrWhiteSpace(sizeText) == string.IsNullOrWhiteSpace(countText))
        {
            Console.Error.WriteLine("Split requires exactly one of --size or --count.");
            return 1;
        }

        var json = IsJson(args);
        var dryRun = IsDryRun(args);
        var quiet = IsQuiet(args);
        var options = ParseCreateOptions(args);
        var maxBytes = sizeText is null ? (long?)null : ParseSize(sizeText);
        var maxCount = countText is null ? (int?)null : int.Parse(countText);
        if (maxBytes is <= 0)
        {
            Console.Error.WriteLine("Split size must be greater than zero.");
            return 1;
        }

        if (maxCount is <= 0)
        {
            Console.Error.WriteLine("Split count must be greater than zero.");
            return 1;
        }

        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(archivePath, "Split archive", IsWrite: false),
            requirePassword: passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(archivePath, password);
        var entries = await RunWithPasswordRetryAsync(
            archivePath,
            "Split archive",
            passwordOptions,
            password,
            resolvedPassword => Task.FromResult(archives.List(archivePath, resolvedPassword))).ConfigureAwait(false);
        var files = entries.Where(x => !x.IsDirectory).OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("Archive contains no files to split.");
            return 1;
        }

        var parts = BuildSplitPlan(files, maxBytes, maxCount);
        if (dryRun)
        {
            WriteCommandResult(
                json,
                "Split dry run completed.",
                new
                {
                    command = "split",
                    dryRun = true,
                    archivePath,
                    outputPrefix,
                    partCount = parts.Count,
                    parts = parts.Select((part, index) => new
                    {
                        index = index + 1,
                        fileCount = part.Count,
                        originalSize = part.Sum(x => x.OriginalSize)
                    })
                });
            return 0;
        }

        var partOutputs = new List<SplitOutputPart>();
        var directoryEntries = entries.Where(x => x.IsDirectory).ToArray();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var outputArchive = BuildSplitOutputPath(outputPrefix, i + 1, Path.GetExtension(archivePath));
            var workspace = CreateTempDirectory("laplace-split");
            try
            {
                var selectedIds = new HashSet<long>(part.Select(x => x.Id));
                if (i == 0)
                {
                    foreach (var directoryEntry in directoryEntries.Where(x => !HasDescendantFile(x.Path, files)))
                    {
                        selectedIds.Add(directoryEntry.Id);
                    }
                }

                await RunWithPasswordRetryAsync(
                    archivePath,
                    "Split archive",
                    passwordOptions,
                    password,
                    async resolvedPassword => await archives.ExtractAsync(
                        archivePath,
                        workspace,
                        new ExtractArchiveOptions
                        {
                            Overwrite = true,
                            VerifyChecksums = true,
                            SelectedEntryIds = selectedIds,
                            Password = resolvedPassword
                        },
                        quiet || json ? null : ProgressToConsole()).ConfigureAwait(false)).ConfigureAwait(false);

                var inputs = Directory.EnumerateFileSystemEntries(workspace).ToArray();
                await archives.CompressAsync(inputs, outputArchive, options, quiet || json ? null : ProgressToConsole()).ConfigureAwait(false);
                partOutputs.Add(new SplitOutputPart(outputArchive, part.Count, part.Sum(x => x.OriginalSize)));
            }
            finally
            {
                TryDeleteDirectory(workspace);
            }
        }

        WriteCommandResult(
            json,
            "Archive split completed.",
            new
            {
                command = "split",
                dryRun = false,
                archivePath,
                outputPrefix,
                partCount = partOutputs.Count,
                parts = partOutputs
            });
        return 0;
    }

    private static async Task<int> ViewAsync(LpcArchiveMutationService mutator, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: laplace view <archive.lpc> <entry_path_or_id> [--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        var archivePath = args[0];
        EnsureLpcArchive(archivePath);
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(passwordOptions, new PasswordRequest(archivePath, "View archive entry", IsWrite: false), passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(archivePath, password);
        var bytes = await RunWithPasswordRetryAsync(
            archivePath,
            "View archive entry",
            passwordOptions,
            password,
            resolvedPassword => Task.FromResult(mutator.ViewFile(archivePath, args[1], resolvedPassword))).ConfigureAwait(false);
        await Console.OpenStandardOutput().WriteAsync(bytes).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RepairAsync(
        RarToolCommandService rarTools,
        LpcRecoveryService recovery,
        string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace repair <archive.lpc|archive.rar>");
            return 1;
        }

        if (IsRarArchive(args[0]))
        {
            await rarTools.RepairAsync(args[0]).ConfigureAwait(false);
            Console.WriteLine("RAR repair command completed.");
            return 0;
        }

        EnsureLpcArchive(args[0]);
        var repairedShards = await recovery.RepairAsync(args[0]).ConfigureAwait(false);
        Console.WriteLine($"LPC repair completed. Reconstructed shards: {repairedShards}.");
        return 0;
    }

    private static async Task<int> BenchmarkAsync(ArchiveWriter writer, ArchiveExtractor extractor, ArchiveReader reader, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace benchmark <input_path>");
            return 1;
        }

        var sourcePath = args[0];
        var json = IsJson(args);
        var archivePath = Path.Combine(Path.GetTempPath(), $"laplace-bench-{Guid.NewGuid():N}.lpc");
        var extractPath = Path.Combine(Path.GetTempPath(), $"laplace-bench-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractPath);

        try
        {
            var compressWatch = Stopwatch.StartNew();
            var archive = await writer.CreateAsync([sourcePath], archivePath, new CreateArchiveOptions
            {
                Mode = CompressionMode.Balanced,
                BlockSizeBytes = 8 * 1024 * 1024,
                VerifyAfterCompression = false
            }).ConfigureAwait(false);
            compressWatch.Stop();

            var extractWatch = Stopwatch.StartNew();
            await extractor.ExtractAsync(archivePath, extractPath, new ExtractArchiveOptions
            {
                Overwrite = true,
                VerifyChecksums = true
            }).ConfigureAwait(false);
            extractWatch.Stop();

            var info = ArchiveInfoBuilder.Build(reader.Read(archivePath));
            var archiveRead = reader.Read(archivePath);
            var rawBlocks = archiveRead.BlockEntries.Count(b => b.IsRaw || b.CompressionMethod == CompressionMethod.Raw);
            var compressedBlocks = archiveRead.BlockEntries.Count - rawBlocks;
            if (json)
            {
                WriteJson(new
                {
                    command = "benchmark",
                    source = sourcePath,
                    originalSize = info.OriginalSize,
                    compressedSize = info.CompressedSize,
                    ratio = info.Ratio,
                    spaceSaved = info.OriginalSize - info.CompressedSize,
                    compressionSeconds = compressWatch.Elapsed.TotalSeconds,
                    decompressionSeconds = extractWatch.Elapsed.TotalSeconds,
                    methods = info.MethodsUsed,
                    rawBlocks,
                    compressedBlocks
                });
            }
            else
            {
                Console.WriteLine("Benchmark:");
                Console.WriteLine($"Source: {sourcePath}");
                Console.WriteLine($"Original size: {info.OriginalSize} bytes");
                Console.WriteLine($"Compressed size: {info.CompressedSize} bytes");
                Console.WriteLine($"Ratio: {info.Ratio:P2}");
                Console.WriteLine($"Space saved: {(info.OriginalSize - info.CompressedSize)} bytes");
                Console.WriteLine($"Compression time: {compressWatch.Elapsed.TotalSeconds:F2}s");
                Console.WriteLine($"Decompression time: {extractWatch.Elapsed.TotalSeconds:F2}s");
                Console.WriteLine($"Compression speed: {FormatSpeed(info.OriginalSize, compressWatch.Elapsed)}");
                Console.WriteLine($"Decompression speed: {FormatSpeed(info.OriginalSize, extractWatch.Elapsed)}");
                Console.WriteLine($"Methods: {string.Join(", ", info.MethodsUsed)}");
                Console.WriteLine($"RAW blocks: {rawBlocks}");
                Console.WriteLine($"Compressed blocks: {compressedBlocks}");
            }

            return 0;
        }
        finally
        {
            TryDelete(archivePath);
            TryDeleteDirectory(extractPath);
        }
    }

    private static CreateArchiveOptions ParseCreateOptions(string[] args)
    {
        var options = new CreateArchiveOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i].ToLowerInvariant();
            if (current == "--verify")
            {
                options.VerifyAfterCompression = true;
            }
            else if (current == "--no-verify")
            {
                options.VerifyAfterCompression = false;
            }
            else if (current == "--mode" && i + 1 < args.Length)
            {
                options.Mode = ParseMode(args[++i]);
            }
            else if (current == "--block-size" && i + 1 < args.Length)
            {
                options.BlockSizeBytes = ParseBlockSize(args[++i]);
            }
            else if (current == "--threads" && i + 1 < args.Length)
            {
                options.Threads = int.Parse(args[++i]);
            }
            else if (current == "--solid" && i + 1 < args.Length)
            {
                options.SolidMode = ParseSolidMode(args[++i]);
            }
            else if (current == "--hide-names")
            {
                options.EncryptMetadata = true;
            }
            else if (current == "--volume-size" && i + 1 < args.Length)
            {
                options.VolumeSizeBytes = ParseSize(args[++i]);
            }
            else if (current == "--recovery-percent" && i + 1 < args.Length)
            {
                options.RecoveryPercent = int.Parse(args[++i]);
            }
        }

        return options;
    }

    private static bool ParseVerifyChecksums(string[] args)
    {
        var verify = true;
        foreach (var arg in args)
        {
            if (arg.Equals("--verify", StringComparison.OrdinalIgnoreCase))
            {
                verify = true;
            }
            else if (arg.Equals("--no-verify", StringComparison.OrdinalIgnoreCase))
            {
                verify = false;
            }
        }

        return verify;
    }

    private static bool IsQuiet(string[] args)
    {
        return args.Any(x => x.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsJson(string[] args)
    {
        return args.Any(x => x.Equals("--json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDryRun(string[] args)
    {
        return args.Any(x => x.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteCommandResult(bool json, string message, object payload)
    {
        if (json)
        {
            WriteJson(payload);
        }
        else
        {
            Console.WriteLine(message);
        }
    }

    private static void WriteJson(object payload)
    {
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static object DescribeCreateOptions(CreateArchiveOptions options)
    {
        return new
        {
            mode = options.Mode.ToString(),
            blockSizeBytes = options.BlockSizeBytes,
            solidMode = options.SolidMode.ToString(),
            verifyAfterCompression = options.VerifyAfterCompression,
            threads = options.Threads,
            encryptMetadata = options.EncryptMetadata,
            volumeSizeBytes = options.VolumeSizeBytes,
            recoveryPercent = options.RecoveryPercent,
            encrypted = options.Password is not null,
            lockArchive = options.LockArchive
        };
    }

    private static object DescribeMutation(string command, string archivePath, string[] operands, MutateArchiveOptions options, bool dryRun)
    {
        return new
        {
            command,
            dryRun,
            archivePath,
            operands,
            options = DescribeMutationOptions(options)
        };
    }

    private static object DescribeMutationOptions(MutateArchiveOptions options)
    {
        return new
        {
            mode = options.Mode.ToString(),
            blockSizeBytes = options.BlockSizeBytes,
            overwrite = options.Overwrite,
            verifyAfterRewrite = options.VerifyAfterRewrite,
            encrypted = options.Password is not null
        };
    }

    private static List<string> ReadFromFileValues(string[] args)
    {
        var values = new List<string>();
        foreach (var path in ReadOptionValues(args, "--from-file"))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                values.Add(trimmed);
            }
        }

        return values;
    }

    private static string[] ReadPatternValues(string[] args, string optionName)
    {
        return ReadOptionValues(args, optionName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? GetSingleOptionValue(string[] args, string optionName)
    {
        return ReadOptionValues(args, optionName).LastOrDefault();
    }

    private static IEnumerable<string> ReadOptionValues(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(optionName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                yield return args[++i];
            }
        }
    }

    private static bool LooksLikeWriteArchivePath(string path)
    {
        return IsSupportedWriteExtension(Path.GetExtension(path));
    }

    private static async Task<HashSet<long>?> ResolveSelectedEntryIdsAsync(
        UniversalArchiveService archives,
        string archivePath,
        ParsedPasswordOptions passwordOptions,
        PasswordContext? password,
        string[] namePatterns)
    {
        if (namePatterns.Length == 0)
        {
            return null;
        }

        var entries = await RunWithPasswordRetryAsync(
            archivePath,
            "List archive",
            passwordOptions,
            password,
            resolvedPassword => Task.FromResult(archives.List(archivePath, resolvedPassword))).ConfigureAwait(false);
        var matches = entries
            .Where(entry => namePatterns.Any(pattern => MatchesSimpleGlob(entry.Path, pattern)))
            .Select(entry => entry.Id)
            .ToHashSet();
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"No archive entries matched: {string.Join(", ", namePatterns)}");
        }

        return matches;
    }

    private static async Task<string[]> ExpandDeleteTargetsAsync(
        UniversalArchiveService archives,
        string archivePath,
        string[] targets,
        ParsedPasswordOptions passwordOptions,
        PasswordContext? password)
    {
        if (!targets.Any(ContainsGlob))
        {
            return targets;
        }

        var entries = await RunWithPasswordRetryAsync(
            archivePath,
            "List archive",
            passwordOptions,
            password,
            resolvedPassword => Task.FromResult(archives.List(archivePath, resolvedPassword))).ConfigureAwait(false);
        var resolved = new List<string>();
        foreach (var target in targets)
        {
            if (!ContainsGlob(target))
            {
                resolved.Add(target);
                continue;
            }

            var matches = entries
                .Where(entry => MatchesSimpleGlob(entry.Path, target))
                .Select(entry => entry.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"No archive entries matched: {target}");
            }

            resolved.AddRange(matches);
        }

        return resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool ContainsGlob(string value)
    {
        return value.Contains('*') || value.Contains('?');
    }

    private static string NormalizeArchiveListingPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static Dictionary<string, string>? TryReadLpcChecksums(
        ArchiveReader reader,
        string archivePath,
        PasswordContext? password)
    {
        if (!IsLpcArchive(archivePath))
        {
            return null;
        }

        var archive = reader.Read(archivePath, password);
        return archive.FileEntries
            .Where(x => !x.IsDirectory && x.FileChecksum.Length > 0)
            .ToDictionary(
                x => NormalizeArchiveListingPath(x.RelativePath),
                x => Convert.ToHexString(x.FileChecksum),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetDiffReason(
        string path,
        ArchiveEntryListing left,
        ArchiveEntryListing right,
        Dictionary<string, string>? leftHashes,
        Dictionary<string, string>? rightHashes)
    {
        if (left.IsDirectory != right.IsDirectory)
        {
            return "type differs";
        }

        if (left.OriginalSize != right.OriginalSize)
        {
            return "size differs";
        }

        if (leftHashes is not null &&
            rightHashes is not null &&
            leftHashes.TryGetValue(path, out var leftHash) &&
            rightHashes.TryGetValue(path, out var rightHash) &&
            !string.Equals(leftHash, rightHash, StringComparison.OrdinalIgnoreCase))
        {
            return "checksum differs";
        }

        return null;
    }

    private static List<List<ArchiveEntryListing>> BuildSplitPlan(
        IReadOnlyList<ArchiveEntryListing> files,
        long? maxBytes,
        int? maxCount)
    {
        var parts = new List<List<ArchiveEntryListing>>();
        var current = new List<ArchiveEntryListing>();
        long currentBytes = 0;
        foreach (var file in files)
        {
            var wouldExceedBytes = maxBytes is not null && current.Count > 0 && currentBytes + file.OriginalSize > maxBytes.Value;
            var wouldExceedCount = maxCount is not null && current.Count >= maxCount.Value;
            if (wouldExceedBytes || wouldExceedCount)
            {
                parts.Add(current);
                current = new List<ArchiveEntryListing>();
                currentBytes = 0;
            }

            current.Add(file);
            currentBytes += file.OriginalSize;
        }

        if (current.Count > 0)
        {
            parts.Add(current);
        }

        return parts;
    }

    private static string BuildSplitOutputPath(string outputPrefix, int partNumber, string inputExtension)
    {
        var extension = Path.GetExtension(outputPrefix);
        var effectiveExtension = IsSupportedWriteExtension(extension)
            ? extension
            : (IsSupportedWriteExtension(inputExtension) ? inputExtension : ".lpc");
        var basePath = IsSupportedWriteExtension(extension)
            ? outputPrefix[..^extension.Length]
            : outputPrefix;
        return $"{basePath}.part{partNumber:000}{effectiveExtension}";
    }

    private static bool HasDescendantFile(string directoryPath, IReadOnlyList<ArchiveEntryListing> files)
    {
        var prefix = NormalizeArchiveListingPath(directoryPath).TrimEnd('/') + "/";
        return files.Any(file => NormalizeArchiveListingPath(file.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<(string ArchivePath, string[] Operands, MutateArchiveOptions Options, ParsedPasswordOptions PasswordOptions)> ParseMutationCommandAsync(
        string[] args,
        string usage)
    {
        var positional = GetPositionalArgs(args);
        var fromFileValues = ReadFromFileValues(args);
        if (positional.Count < 2 && !(positional.Count == 1 && fromFileValues.Count > 0))
        {
            Console.Error.WriteLine($"Usage: {usage}");
            throw new ArgumentException("Missing required command arguments.");
        }

        var archivePath = positional[0];
        var passwordOptions = ParsePasswordOptions(args);
        var options = await ParseMutationOptionsAsync(archivePath, args, passwordOptions).ConfigureAwait(false);
        var operands = positional.Skip(1)
            .Concat(fromFileValues)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (archivePath, operands, options, passwordOptions);
    }

    private static async Task<MutateArchiveOptions> ParseMutationOptionsAsync(
        string archivePath,
        string[] args,
        ParsedPasswordOptions passwordOptions)
    {
        var createOptions = ParseCreateOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(archivePath, "Mutate archive", IsWrite: true),
            passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(archivePath, password);

        return new MutateArchiveOptions
        {
            Mode = createOptions.Mode,
            BlockSizeBytes = createOptions.BlockSizeBytes,
            Password = password,
            VerifyAfterRewrite = createOptions.VerifyAfterCompression
        };
    }

    private static List<string> GetPositionalArgs(string[] args)
    {
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (RequiresValue(args[i]) && i + 1 < args.Length)
                {
                    i++;
                }

                continue;
            }

            positional.Add(args[i]);
        }

        return positional;
    }

    private static bool RequiresValue(string option)
    {
        return option.Equals("--mode", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--block-size", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--threads", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--solid", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--from-file", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--password", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--password-file", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--keyfile", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--set", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--file", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--name", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--text", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--size", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--count", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--volume-size", StringComparison.OrdinalIgnoreCase) ||
               option.Equals("--recovery-percent", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ParsedPasswordOptions
    {
        public string? Password { get; init; }
        public string? PasswordFile { get; init; }
        public string? KeyfilePath { get; init; }
        public bool EncryptRequested { get; init; }
        public bool HasExplicitSecret => Password is not null || PasswordFile is not null || KeyfilePath is not null;
        public bool HasKeyfile => KeyfilePath is not null;
    }

    private sealed record ArchiveDiffItem(
        string Path,
        string Status,
        bool IsDirectory,
        long? LeftSize,
        long? RightSize,
        string Reason);

    private sealed record SplitOutputPart(string OutputArchive, int FileCount, long OriginalSize);

    private static ParsedPasswordOptions ParsePasswordOptions(string[] args)
    {
        string? password = null;
        string? passwordFile = null;
        string? keyfilePath = null;
        var encrypt = false;

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            if (current.Equals("--encrypt", StringComparison.OrdinalIgnoreCase))
            {
                encrypt = true;
            }
            else if (current.Equals("--password", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--password requires a value.");
                }

                password = args[++i];
            }
            else if (current.Equals("--password-file", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--password-file requires a path.");
                }

                passwordFile = args[++i];
            }
            else if (current.Equals("--keyfile", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--keyfile requires a path.");
                }

                keyfilePath = args[++i];
            }
        }

        return new ParsedPasswordOptions
        {
            Password = password,
            PasswordFile = passwordFile,
            KeyfilePath = keyfilePath,
            EncryptRequested = encrypt
        };
    }

    private static async Task<PasswordContext?> ResolvePasswordAsync(
        ParsedPasswordOptions options,
        PasswordRequest request,
        bool requirePassword,
        bool confirmInteractivePassword = false)
    {
        var explicitSecret = ReadExplicitSecret(options);
        if (explicitSecret is not null)
        {
            return explicitSecret;
        }

        if (!requirePassword)
        {
            return null;
        }

        var provider = CreatePasswordProvider();
        var password = await provider.GetPasswordAsync(request).ConfigureAwait(false);
        if (password is null)
        {
            throw new ArchivePasswordRequiredException(request.ArchivePath);
        }

        if (confirmInteractivePassword)
        {
            var confirmation = await provider.GetPasswordAsync(request with { Operation = "Confirm archive" }).ConfigureAwait(false);
            if (confirmation is null)
            {
                throw new ArchivePasswordRequiredException(request.ArchivePath);
            }

            ArchivePasswordPolicy.EnsureConfirmationMatches(password, confirmation);
        }

        return password;
    }

    private static PasswordContext? ReadExplicitSecret(ParsedPasswordOptions options)
    {
        var password = options.Password;
        if (options.PasswordFile is not null)
        {
            password = File.ReadAllText(options.PasswordFile).TrimEnd('\r', '\n');
            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Password file is empty.");
            }
        }

        var keyfileHash = ReadKeyfileHash(options.KeyfilePath);
        if (password is null && keyfileHash is null)
        {
            return null;
        }

        return new PasswordContext(password, keyfileHash);
    }

    private static byte[]? ReadKeyfileHash(string? keyfilePath)
    {
        if (keyfilePath is null)
        {
            return null;
        }

        var keyfileBytes = File.ReadAllBytes(keyfilePath);
        try
        {
            if (keyfileBytes.Length == 0)
            {
                throw new ArgumentException("Keyfile is empty.");
            }

            return SHA256.HashData(keyfileBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyfileBytes);
        }
    }

    private static IPasswordProvider CreatePasswordProvider()
    {
        var interactive = Environment.UserInteractive && !Console.IsInputRedirected;
        if (!interactive)
        {
            return new FallbackPasswordProvider();
        }

        if (OperatingSystem.IsWindows())
        {
            return new FallbackPasswordProvider(new WindowsPopupPasswordProvider(), new ConsolePasswordProvider());
        }

        return new ConsolePasswordProvider();
    }

    private static async Task RunWithPasswordRetryAsync(
        string archivePath,
        string operation,
        ParsedPasswordOptions passwordOptions,
        PasswordContext? password,
        Func<PasswordContext?, Task> action)
    {
        await RunWithPasswordRetryAsync<object?>(
            archivePath,
            operation,
            passwordOptions,
            password,
            async resolvedPassword =>
            {
                await action(resolvedPassword).ConfigureAwait(false);
                return null;
            }).ConfigureAwait(false);
    }

    private static async Task<T> RunWithPasswordRetryAsync<T>(
        string archivePath,
        string operation,
        ParsedPasswordOptions passwordOptions,
        PasswordContext? password,
        Func<PasswordContext?, Task<T>> action)
    {
        try
        {
            return await action(password).ConfigureAwait(false);
        }
        catch (ArchivePasswordRequiredException) when (password is null)
        {
            var promptedPassword = await ResolvePasswordAsync(
                passwordOptions,
                new PasswordRequest(archivePath, operation, IsWrite: false, IsRetry: true),
                requirePassword: true).ConfigureAwait(false);
            return await action(promptedPassword).ConfigureAwait(false);
        }
        catch (ArchivePasswordException) when (!passwordOptions.HasExplicitSecret)
        {
            var promptedPassword = await ResolvePasswordAsync(
                passwordOptions,
                new PasswordRequest(archivePath, operation, IsWrite: false, IsRetry: true),
                requirePassword: true).ConfigureAwait(false);
            return await action(promptedPassword).ConfigureAwait(false);
        }
    }

    private static bool IsPasswordRequiredMessage(string message)
    {
        return message.Contains("requires a password", StringComparison.OrdinalIgnoreCase);
    }

    private static CompressionMode ParseMode(string mode)
    {
        return mode.ToLowerInvariant() switch
        {
            "fast" => CompressionMode.Fast,
            "balanced" => CompressionMode.Balanced,
            "maximum" => CompressionMode.Maximum,
            "intensive" => CompressionMode.Intensive,
            "compressed" or "ultra" => CompressionMode.Compressed,
            "auto" => CompressionMode.Auto,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Unknown mode: {mode}")
        };
    }

    private static int ParseBlockSize(string token)
    {
        var normalized = token.Trim().ToUpperInvariant();
        if (!normalized.EndsWith("M"))
        {
            throw new ArgumentException($"Invalid block size value: {token}. Example: 8M");
        }

        var numberPart = normalized[..^1];
        var mb = int.Parse(numberPart);
        if (mb is not (4 or 8 or 16 or 32 or 64))
        {
            throw new ArgumentException("Block size must be one of: 4M, 8M, 16M, 32M, 64M");
        }

        return mb * 1024 * 1024;
    }

    private static long ParseSize(string token)
    {
        var normalized = token.Trim().ToUpperInvariant();
        var multiplier = normalized.EndsWith("G", StringComparison.Ordinal) ? 1024L * 1024L * 1024L :
            normalized.EndsWith("M", StringComparison.Ordinal) ? 1024L * 1024L :
            normalized.EndsWith("K", StringComparison.Ordinal) ? 1024L : 1L;
        var number = multiplier == 1 ? normalized : normalized[..^1];
        return long.Parse(number) * multiplier;
    }

    private static SolidMode ParseSolidMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "on" => SolidMode.On,
            "off" => SolidMode.Off,
            "auto" => SolidMode.Auto,
            _ => throw new ArgumentOutOfRangeException(nameof(value), $"Unknown solid mode: {value}")
        };
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Laplace CLI");
        Console.WriteLine("Global options on supported commands: --json for machine-readable output, --dry-run for non-mutating previews, --from-file for newline-delimited operand lists.");
        Console.WriteLine("Commands:");
        Console.WriteLine("  laplace compress <input_path...> [output.lpc|output.zip|output.7z|output.rar] [--from-file <path>] [--mode fast|balanced|maximum|intensive|compressed|auto] [--block-size 8M] [--solid on|off|auto] [--threads N] [--hide-names] [--recovery-percent N] [--verify|--no-verify] [--quiet] [--json] [--dry-run] [--encrypt|--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace compress-beside <input_path> [--mode fast|balanced|maximum|intensive|compressed|auto] [--block-size 8M] [--solid on|off|auto] [--threads N] [--hide-names] [--recovery-percent N] [--verify|--no-verify] [--quiet] [--json] [--dry-run] [--encrypt|--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace estimate <input_path...> [--mode fast|balanced|maximum|intensive|compressed|auto] [--block-size 8M] [--solid on|off|auto] [--threads N] [--json]");
        Console.WriteLine("  laplace extract <input_archive> <output_folder> [--name <glob>] [--overwrite] [--verify|--no-verify] [--quiet] [--json] [--dry-run] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace list <input_archive> [--json] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace info <input_archive> [--json] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace test <input_archive> [--json] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace add <archive> <input_path...> [--from-file <path>] [--mode fast|balanced|maximum|intensive|compressed|auto] [--json] [--dry-run] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace freshen <archive> <input_path...> [--from-file <path>] [--json] [--dry-run] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace delete <archive> <entry_path_or_id_or_glob...> [--from-file <path>] [--json] [--dry-run] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace rename <archive.lpc> <entry_path_or_id> <new_entry_path> [--json] [--dry-run] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace comment <archive> --show|--set <text>|--file <path>|--clear [--json] [--dry-run] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace lock <archive> [--json] [--dry-run] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace find <archive> [--name <glob>] [--text <value>] [--json] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace diff <archive_a> <archive_b> [--json] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace merge <output_archive> <input_archive...> [--from-file <path>] [--mode fast|balanced|maximum|intensive|compressed|auto] [--json] [--dry-run] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace split <archive> <output_prefix> (--size 700M|--count 100) [--mode fast|balanced|maximum|intensive|compressed|auto] [--json] [--dry-run] [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace view <archive.lpc> <entry_path_or_id> [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace repair <archive.lpc|archive.rar>");
        Console.WriteLine("  laplace benchmark <input_path> [--json]");
        Console.WriteLine("  laplace open <archive.lpc>");
        Console.WriteLine("  laplace extract-here <archive> [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace extract-to-folder <archive> <output_folder> [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace extract-to-named-folder <archive> [--password <value>|--password-file <path>|--keyfile <path>]");
        Console.WriteLine("  laplace extract-dialog <archive>");
        Console.WriteLine("  laplace iso-to-drive-dialog <image.iso>");
        Console.WriteLine("  laplace integrate install|uninstall|status [--cli-path <path-to-laplace.exe>]");
    }

    private static IProgress<ArchiveOperationProgress> ProgressToConsole()
    {
        return new Progress<ArchiveOperationProgress>(p =>
        {
            Console.Write($"\r{p.Percent,6:F2}%  {p.CurrentItem}                                 ");
        });
    }

    private static void PrintSizeStats(long originalBytes, long compressedBytes, TimeSpan elapsed)
    {
        var ratio = originalBytes == 0 ? 1 : (double)compressedBytes / originalBytes;
        var saved = originalBytes - compressedBytes;
        Console.WriteLine($"Original size: {originalBytes} bytes");
        Console.WriteLine($"Compressed size: {compressedBytes} bytes");
        Console.WriteLine($"Compression ratio: {ratio:P2}");
        Console.WriteLine($"Space saved: {saved} bytes");
        Console.WriteLine($"Time used: {elapsed.TotalSeconds:F2}s");
        Console.WriteLine($"Compression speed: {FormatSpeed(originalBytes, elapsed)}");
    }

    private static long GetInputSize(IEnumerable<string> inputPaths)
    {
        long total = 0;
        foreach (var path in inputPaths)
        {
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
            else if (Directory.Exists(path))
            {
                total += Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(file => new FileInfo(file).Length);
            }
        }

        return total;
    }

    private static void PrintArchiveSummary(string archivePath, ArchiveSummary info)
    {
        Console.WriteLine($"Archive: {archivePath}");
        Console.WriteLine($"Format: {info.Format}");
        if (info.ArchiveVersion > 0)
        {
            Console.WriteLine($"Version: {info.ArchiveVersion}");
        }

        Console.WriteLine($"Encrypted: {info.IsEncrypted}");
        Console.WriteLine($"Metadata encrypted: {info.IsMetadataEncrypted}");
        Console.WriteLine($"Locked: {info.IsLocked}");
        Console.WriteLine($"Recovery record: {info.HasRecoveryRecord}");
        if (!string.IsNullOrEmpty(info.Comment))
        {
            Console.WriteLine($"Comment: {info.Comment}");
        }

        Console.WriteLine($"Files: {info.FileCount}");
        Console.WriteLine($"Folders: {info.FolderCount}");
        Console.WriteLine($"Blocks/entries: {info.BlockCount}");
        if (info.CreatedUtc is not null)
        {
            Console.WriteLine($"Created (UTC): {info.CreatedUtc:O}");
        }

        Console.WriteLine($"Methods: {string.Join(", ", info.MethodsUsed)}");
        Console.WriteLine($"Original size: {info.OriginalSize} bytes");
        Console.WriteLine($"Compressed size: {info.CompressedSize} bytes");
        Console.WriteLine($"Ratio: {info.Ratio:P2}");
        Console.WriteLine($"Space saved: {(info.OriginalSize - info.CompressedSize)} bytes");
        if (!string.IsNullOrWhiteSpace(info.Notes))
        {
            Console.WriteLine($"Notes: {info.Notes}");
        }
    }

    private static void PrintArchiveEstimate(ArchiveEstimate estimate)
    {
        Console.WriteLine("Compression estimate:");
        Console.WriteLine($"Files: {estimate.FileCount}");
        Console.WriteLine($"Folders: {estimate.FolderCount}");
        Console.WriteLine($"Original size: {estimate.OriginalSize} bytes ({FormatBytes(estimate.OriginalSize)})");
        Console.WriteLine($"Estimated archive size: {estimate.EstimatedCompressedSize} bytes ({FormatBytes(estimate.EstimatedCompressedSize)})");
        Console.WriteLine($"Estimated ratio: {estimate.EstimatedRatio:P2}");
        Console.WriteLine($"Estimated reduction: {estimate.EstimatedReduction:P2}");
        Console.WriteLine($"Sample count: {estimate.SampleCount}");
        Console.WriteLine($"Confidence: {estimate.Confidence}");
        Console.WriteLine($"Likely methods: {string.Join(", ", estimate.LikelyMethods)}");
        if (!string.IsNullOrWhiteSpace(estimate.Notes))
        {
            Console.WriteLine($"Notes: {estimate.Notes}");
        }
    }

    private static string FormatSpeed(long bytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0.0001)
        {
            return "n/a";
        }

        var mbps = bytes / elapsed.TotalSeconds / (1024d * 1024d);
        return $"{mbps:F2} MB/s";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:F2} {units[unit]}";
    }

    private static bool IsSupportedWriteExtension(string extension)
    {
        return extension.Equals(".lpc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".rar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLpcArchive(string archivePath)
    {
        return ArchiveFormatDetector.DetectReadKind(archivePath) == SupportedArchiveKind.Lpc;
    }

    private static bool IsRarArchive(string archivePath)
    {
        return ArchiveFormatDetector.DetectReadKind(archivePath) == SupportedArchiveKind.Rar;
    }

    private static void ValidatePasswordContextForArchiveRead(string archivePath, PasswordContext? password)
    {
        if (password?.HasKeyfile == true && !IsLpcArchive(archivePath))
        {
            throw new NotSupportedException("Keyfiles are supported for LPC archives only.");
        }
    }

    private static void ValidatePasswordContextForArchiveWrite(string archivePath, PasswordContext? password)
    {
        if (password?.HasKeyfile == true && ArchiveFormatDetector.DetectWriteKind(archivePath) != SupportedArchiveKind.Lpc)
        {
            throw new NotSupportedException("Keyfiles are supported for LPC archives only.");
        }
    }

    private static void EnsureLpcArchive(string archivePath)
    {
        if (!IsLpcArchive(archivePath))
        {
            throw new NotSupportedException("This mutation command is supported natively only for .lpc archives. RAR support is delegated only where explicitly implemented.");
        }
    }

    private static bool MatchesSimpleGlob(string value, string glob)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(glob)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static int OpenCommand(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace open <archive.lpc>");
            return 1;
        }

        if (TryLaunchDesktop(["--open", args[0]]))
        {
            return 0;
        }

        Console.WriteLine("Laplace desktop UI was not found next to the CLI. Use `laplace list` or `laplace info`, or run `dotnet run --project .\\src\\Laplace.Desktop\\Laplace.Desktop.csproj`.");
        return 0;
    }

    private static async Task<int> ExtractHereAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace extract-here <archive> [--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        var archivePath = Path.GetFullPath(args[0]);
        var target = Path.GetDirectoryName(archivePath) ?? Directory.GetCurrentDirectory();
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(passwordOptions, new PasswordRequest(archivePath, "Extract archive", IsWrite: false), passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(archivePath, password);
        await RunWithPasswordRetryAsync(
            archivePath,
            "Extract archive",
            passwordOptions,
            password,
            async resolvedPassword => await archives.ExtractAsync(archivePath, target, new ExtractArchiveOptions
            {
                Overwrite = false,
                VerifyChecksums = true,
                Password = resolvedPassword
            }).ConfigureAwait(false)).ConfigureAwait(false);
        Console.WriteLine($"Extracted to: {target}");
        return 0;
    }

    private static async Task<int> ExtractToFolderAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: laplace extract-to-folder <archive> <output_folder> [--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(passwordOptions, new PasswordRequest(args[0], "Extract archive", IsWrite: false), passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(args[0], password);
        await RunWithPasswordRetryAsync(
            args[0],
            "Extract archive",
            passwordOptions,
            password,
            async resolvedPassword => await archives.ExtractAsync(args[0], args[1], new ExtractArchiveOptions
            {
                Overwrite = false,
                VerifyChecksums = true,
                Password = resolvedPassword
            }).ConfigureAwait(false)).ConfigureAwait(false);
        Console.WriteLine($"Extracted to: {args[1]}");
        return 0;
    }

    private static async Task<int> ExtractToNamedFolderAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace extract-to-named-folder <archive> [--password <value>|--password-file <path>|--keyfile <path>]");
            return 1;
        }

        var archivePath = Path.GetFullPath(args[0]);
        var folder = Path.Combine(Path.GetDirectoryName(archivePath) ?? Directory.GetCurrentDirectory(), Path.GetFileNameWithoutExtension(archivePath));
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(passwordOptions, new PasswordRequest(archivePath, "Extract archive", IsWrite: false), passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        ValidatePasswordContextForArchiveRead(archivePath, password);
        await RunWithPasswordRetryAsync(
            archivePath,
            "Extract archive",
            passwordOptions,
            password,
            async resolvedPassword => await archives.ExtractAsync(archivePath, folder, new ExtractArchiveOptions
            {
                Overwrite = false,
                VerifyChecksums = true,
                Password = resolvedPassword
            }).ConfigureAwait(false)).ConfigureAwait(false);
        Console.WriteLine($"Extracted to: {folder}");
        return 0;
    }

    private static int CompressDialogCommand(string[] args)
    {
        var launchArgs = new[] { "--add" }.Concat(args).ToArray();
        if (TryLaunchDesktop(launchArgs))
        {
            return 0;
        }

        Console.WriteLine("Laplace desktop UI was not found next to the CLI.");
        Console.WriteLine($"Selected arguments: {string.Join(" ", args)}");
        return 0;
    }

    private static int ExtractDialogCommand(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace extract-dialog <archive>");
            return 1;
        }

        if (TryLaunchDesktop(["--extract", args[0]]))
        {
            return 0;
        }

        Console.WriteLine("Laplace desktop UI was not found next to the CLI. Use `laplace extract-here` or `laplace extract-to-folder` instead.");
        return 0;
    }

    private static int IsoToDriveDialogCommand(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace iso-to-drive-dialog <image.iso>");
            return 1;
        }

        if (TryLaunchDesktop(["--iso-to-drive", args[0]]))
        {
            return 0;
        }

        Console.WriteLine("Laplace desktop UI was not found next to the CLI. Use `laplace extract-to-folder` and choose a removable drive instead.");
        return 0;
    }

    private static int IntegrateCommand(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: laplace integrate install|uninstall|status [--cli-path <path>]");
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Shell integration is only supported on Windows.");
            return 1;
        }

        var manager = new ShellIntegrationManager();
        var action = args[0].ToLowerInvariant();
        if (action == "status")
        {
            var status = manager.GetStatus();
            Console.WriteLine($"Installed: {status.IsInstalled}");
            Console.WriteLine($"Extension associated (.lpc): {status.ExtensionAssociated}");
            Console.WriteLine($"Open command: {status.OpenCommand}");
            Console.WriteLine($"Registered archive verbs: {status.RegisteredLaplaceVerbCount}");
            return 0;
        }

        if (action == "install")
        {
            var cliPath = ResolveCliPathArg(args.Skip(1).ToArray()) ?? Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(cliPath))
            {
                Console.Error.WriteLine("Could not resolve CLI executable path. Pass --cli-path explicitly.");
                return 1;
            }

            manager.Install(cliPath);
            Console.WriteLine("Laplace shell integration installed for current user.");
            return 0;
        }

        if (action == "uninstall")
        {
            manager.Uninstall();
            Console.WriteLine("Laplace shell integration removed for current user.");
            return 0;
        }

        Console.Error.WriteLine($"Unknown integrate action: {action}");
        return 1;
    }

    private static string? ResolveCliPathArg(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--cli-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool TryLaunchDesktop(IEnumerable<string> arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var guiPath = ResolveDesktopPath();
        if (guiPath is null)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = guiPath,
            UseShellExecute = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);
        return true;
    }

    private static string? ResolveDesktopPath()
    {
        var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        var directory = string.IsNullOrWhiteSpace(executable) ? AppContext.BaseDirectory : Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var candidate = Path.Combine(directory, "laplace-gui.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }
}
