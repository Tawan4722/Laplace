using Laplace.Compression;
using Laplace.Core.Abstractions;
using Laplace.Core.Enums;
using Laplace.Core.Exceptions;
using Laplace.Core.Models;
using Laplace.Core.Services;
using Laplace.ShellIntegration;
using System.Diagnostics;

namespace Laplace.Cli;

internal static class Program
{
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

        try
        {
            var command = args[0].ToLowerInvariant();
            var remaining = args.Skip(1).ToArray();
            return command switch
            {
                "compress" => await CompressAsync(archives, remaining).ConfigureAwait(false),
                "compress-beside" => await CompressBesideAsync(archives, remaining).ConfigureAwait(false),
                "extract" => await ExtractAsync(archives, remaining).ConfigureAwait(false),
                "list" => await ListAsync(archives, remaining).ConfigureAwait(false),
                "info" => await InfoAsync(archives, remaining).ConfigureAwait(false),
                "test" => await TestAsync(archives, remaining).ConfigureAwait(false),
                "benchmark" => await BenchmarkAsync(writer, extractor, reader, remaining).ConfigureAwait(false),
                "open" => OpenCommand(remaining),
                "extract-here" => await ExtractHereAsync(archives, remaining).ConfigureAwait(false),
                "extract-to-folder" => await ExtractToFolderAsync(archives, remaining).ConfigureAwait(false),
                "extract-to-named-folder" => await ExtractToNamedFolderAsync(archives, remaining).ConfigureAwait(false),
                "extract-dialog" => ExtractDialogCommand(remaining),
                "compress-dialog" => CompressDialogCommand(remaining),
                "integrate" => IntegrateCommand(remaining),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"laplace error: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> CompressAsync(UniversalArchiveService archives, string[] args)
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

        if (positional.Count < 2)
        {
            Console.Error.WriteLine("Usage: laplace compress <input_path...> <output.lpc|output.zip> [options] [--encrypt|--password <value>|--password-file <path>]");
            return 1;
        }

        var outputPath = positional[^1];
        var inputPaths = positional.Take(positional.Count - 1).ToArray();
        var optionArgs = args.Skip(optionStart).ToArray();
        var options = ParseCreateOptions(optionArgs);
        var passwordOptions = ParsePasswordOptions(optionArgs);
        if (passwordOptions.EncryptRequested || passwordOptions.HasExplicitSecret)
        {
            options.Password = await ResolvePasswordAsync(
                passwordOptions,
                new PasswordRequest(outputPath, "Create archive", IsWrite: true),
                requirePassword: true).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"Compressing {inputPaths.Length} input path(s) -> '{outputPath}'");

        await archives.CompressAsync(inputPaths, outputPath, options, ProgressToConsole()).ConfigureAwait(false);
        stopwatch.Stop();

        var info = archives.Info(outputPath, options.Password);
        Console.WriteLine();
        Console.WriteLine("Compression completed.");
        PrintSizeStats(info.OriginalSize, info.CompressedSize, stopwatch.Elapsed);

        if (options.VerifyAfterCompression)
        {
            var testResult = await archives.TestAsync(outputPath, options.Password).ConfigureAwait(false);
            Console.WriteLine(testResult.Success ? "Verification: OK" : $"Verification: FAILED ({testResult.Message})");
        }

        return 0;
    }

    private static async Task<int> CompressBesideAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace compress-beside <input_path> [options] [--encrypt|--password <value>|--password-file <path>]");
            return 1;
        }

        var inputPath = Path.GetFullPath(args[0]);
        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            Console.Error.WriteLine($"Input path not found: {inputPath}");
            return 1;
        }

        var optionArgs = args.Skip(1).ToArray();
        var outputPath = ResolveBesideArchivePath(inputPath);
        var options = ParseCreateOptions(optionArgs);
        var passwordOptions = ParsePasswordOptions(optionArgs);
        if (passwordOptions.EncryptRequested || passwordOptions.HasExplicitSecret)
        {
            options.Password = await ResolvePasswordAsync(
                passwordOptions,
                new PasswordRequest(outputPath, "Create archive", IsWrite: true),
                requirePassword: true).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"Compressing '{inputPath}' -> '{outputPath}'");

        await archives.CompressAsync([inputPath], outputPath, options, ProgressToConsole()).ConfigureAwait(false);
        stopwatch.Stop();

        var info = archives.Info(outputPath, options.Password);
        Console.WriteLine();
        Console.WriteLine("Compression completed.");
        PrintSizeStats(info.OriginalSize, info.CompressedSize, stopwatch.Elapsed);

        if (options.VerifyAfterCompression)
        {
            var testResult = await archives.TestAsync(outputPath, options.Password).ConfigureAwait(false);
            Console.WriteLine(testResult.Success ? "Verification: OK" : $"Verification: FAILED ({testResult.Message})");
        }

        return 0;
    }

    private static async Task<int> ExtractAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: laplace extract <input_archive> <output_folder> [--overwrite] [--password <value>|--password-file <path>]");
            return 1;
        }

        var inputArchive = args[0];
        var outputFolder = args[1];
        var overwrite = args.Any(x => x.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(inputArchive, "Extract archive", IsWrite: false),
            requirePassword: passwordOptions.HasExplicitSecret).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"Extracting '{inputArchive}' -> '{outputFolder}'");

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
                    VerifyChecksums = true,
                    Password = resolvedPassword
                },
                ProgressToConsole()).ConfigureAwait(false)).ConfigureAwait(false);

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"Extraction completed in {stopwatch.Elapsed.TotalSeconds:F2}s.");
        return 0;
    }

    private static async Task<int> ListAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace list <input_archive> [--password <value>|--password-file <path>]");
            return 1;
        }

        var inputArchive = args[0];
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(inputArchive, "List archive", IsWrite: false),
            requirePassword: passwordOptions.HasExplicitSecret).ConfigureAwait(false);

        var entries = await RunWithPasswordRetryAsync(
            inputArchive,
            "List archive",
            passwordOptions,
            password,
            resolvedPassword => Task.FromResult(archives.List(inputArchive, resolvedPassword))).ConfigureAwait(false);

        Console.WriteLine($"Archive: {inputArchive}");
        Console.WriteLine("ID\tType\tOriginal\tCompressed\tMethod\tEncrypted\tPath");
        foreach (var entry in entries)
        {
            var type = entry.IsDirectory ? "DIR" : "FILE";
            Console.WriteLine($"{entry.Id}\t{type}\t{entry.OriginalSize}\t{entry.CompressedSize}\t{entry.Method}\t{entry.IsEncrypted}\t{entry.Path}");
        }

        return 0;
    }

    private static async Task<int> InfoAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace info <input_archive> [--password <value>|--password-file <path>]");
            return 1;
        }

        var inputArchive = args[0];
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(inputArchive, "Read archive info", IsWrite: false),
            requirePassword: passwordOptions.HasExplicitSecret).ConfigureAwait(false);

        var info = await RunWithPasswordRetryAsync(
            inputArchive,
            "Read archive info",
            passwordOptions,
            password,
            resolvedPassword => Task.FromResult(archives.Info(inputArchive, resolvedPassword))).ConfigureAwait(false);

        PrintArchiveSummary(inputArchive, info);
        return 0;
    }

    private static async Task<int> TestAsync(UniversalArchiveService archives, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace test <input_archive> [--password <value>|--password-file <path>]");
            return 1;
        }

        var inputArchive = args[0];
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(
            passwordOptions,
            new PasswordRequest(inputArchive, "Test archive", IsWrite: false),
            requirePassword: passwordOptions.HasExplicitSecret).ConfigureAwait(false);

        Console.WriteLine($"Testing archive: {inputArchive}");
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

        Console.WriteLine();
        if (result.Success)
        {
            Console.WriteLine($"Integrity OK. Files: {result.FileCount}, Blocks: {result.BlockCount}");
            return 0;
        }

        Console.Error.WriteLine($"Integrity FAILED: {result.Message}");
        return 2;
    }

    private static async Task<int> BenchmarkAsync(ArchiveWriter writer, ArchiveExtractor extractor, ArchiveReader reader, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace benchmark <input_path>");
            return 1;
        }

        var sourcePath = args[0];
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
        }

        return options;
    }

    private sealed class ParsedPasswordOptions
    {
        public string? Password { get; init; }
        public string? PasswordFile { get; init; }
        public bool EncryptRequested { get; init; }
        public bool HasExplicitSecret => Password is not null || PasswordFile is not null;
    }

    private static ParsedPasswordOptions ParsePasswordOptions(string[] args)
    {
        string? password = null;
        string? passwordFile = null;
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
        }

        return new ParsedPasswordOptions
        {
            Password = password,
            PasswordFile = passwordFile,
            EncryptRequested = encrypt
        };
    }

    private static async Task<PasswordContext?> ResolvePasswordAsync(
        ParsedPasswordOptions options,
        PasswordRequest request,
        bool requirePassword)
    {
        var explicitPassword = ReadExplicitPassword(options);
        if (explicitPassword is not null)
        {
            return explicitPassword;
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

        return password;
    }

    private static PasswordContext? ReadExplicitPassword(ParsedPasswordOptions options)
    {
        if (options.Password is not null)
        {
            return new PasswordContext(options.Password);
        }

        if (options.PasswordFile is null)
        {
            return null;
        }

        var value = File.ReadAllText(options.PasswordFile).TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("Password file is empty.");
        }

        return new PasswordContext(value);
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
        Console.WriteLine("Commands:");
        Console.WriteLine("  laplace compress <input_path...> <output.lpc|output.zip> [--mode fast|balanced|maximum|auto] [--block-size 8M] [--solid on|off|auto] [--threads N] [--verify] [--encrypt|--password <value>|--password-file <path>]");
        Console.WriteLine("  laplace compress-beside <input_path> [--mode fast|balanced|maximum|auto] [--block-size 8M] [--solid on|off|auto] [--threads N] [--verify] [--encrypt|--password <value>|--password-file <path>]");
        Console.WriteLine("  laplace extract <input_archive> <output_folder> [--overwrite] [--password <value>|--password-file <path>]");
        Console.WriteLine("  laplace list <input_archive> [--password <value>|--password-file <path>]");
        Console.WriteLine("  laplace info <input_archive> [--password <value>|--password-file <path>]");
        Console.WriteLine("  laplace test <input_archive> [--password <value>|--password-file <path>]");
        Console.WriteLine("  laplace benchmark <input_path>");
        Console.WriteLine("  laplace open <archive.lpc>");
        Console.WriteLine("  laplace extract-here <archive> [--password <value>|--password-file <path>]");
        Console.WriteLine("  laplace extract-to-folder <archive> <output_folder> [--password <value>|--password-file <path>]");
        Console.WriteLine("  laplace extract-to-named-folder <archive> [--password <value>|--password-file <path>]");
        Console.WriteLine("  laplace extract-dialog <archive>");
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

    private static void PrintArchiveSummary(string archivePath, ArchiveSummary info)
    {
        Console.WriteLine($"Archive: {archivePath}");
        Console.WriteLine($"Format: {info.Format}");
        if (info.ArchiveVersion > 0)
        {
            Console.WriteLine($"Version: {info.ArchiveVersion}");
        }

        Console.WriteLine($"Encrypted: {info.IsEncrypted}");
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

    private static string FormatSpeed(long bytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0.0001)
        {
            return "n/a";
        }

        var mbps = bytes / elapsed.TotalSeconds / (1024d * 1024d);
        return $"{mbps:F2} MB/s";
    }

    private static string ResolveBesideArchivePath(string inputPath)
    {
        var fullPath = Path.GetFullPath(inputPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            parent = Directory.GetCurrentDirectory();
        }

        var name = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath).Name
            : Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return GetAvailableArchivePath(Path.Combine(parent, $"{name}.lpc"));
    }

    private static string GetAvailableArchivePath(string preferredPath)
    {
        if (!File.Exists(preferredPath) && !Directory.Exists(preferredPath))
        {
            return preferredPath;
        }

        var directory = Path.GetDirectoryName(preferredPath) ?? Directory.GetCurrentDirectory();
        var name = Path.GetFileNameWithoutExtension(preferredPath);
        var extension = Path.GetExtension(preferredPath);
        for (var i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not find an available archive name beside: {preferredPath}");
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
            Console.Error.WriteLine("Usage: laplace extract-here <archive> [--password <value>|--password-file <path>]");
            return 1;
        }

        var archivePath = Path.GetFullPath(args[0]);
        var target = Path.GetDirectoryName(archivePath) ?? Directory.GetCurrentDirectory();
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(passwordOptions, new PasswordRequest(archivePath, "Extract archive", IsWrite: false), passwordOptions.HasExplicitSecret).ConfigureAwait(false);
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
            Console.Error.WriteLine("Usage: laplace extract-to-folder <archive> <output_folder> [--password <value>|--password-file <path>]");
            return 1;
        }

        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(passwordOptions, new PasswordRequest(args[0], "Extract archive", IsWrite: false), passwordOptions.HasExplicitSecret).ConfigureAwait(false);
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
            Console.Error.WriteLine("Usage: laplace extract-to-named-folder <archive> [--password <value>|--password-file <path>]");
            return 1;
        }

        var archivePath = Path.GetFullPath(args[0]);
        var folder = Path.Combine(Path.GetDirectoryName(archivePath) ?? Directory.GetCurrentDirectory(), Path.GetFileNameWithoutExtension(archivePath));
        var passwordOptions = ParsePasswordOptions(args);
        var password = await ResolvePasswordAsync(passwordOptions, new PasswordRequest(archivePath, "Extract archive", IsWrite: false), passwordOptions.HasExplicitSecret).ConfigureAwait(false);
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
