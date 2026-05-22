using Laplace.Compression;
using Laplace.Core.Enums;
using Laplace.Core.Models;
using Laplace.Core.Services;
using Laplace.ShellIntegration;
using System.Diagnostics;

namespace Laplace.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var registry = new CompressorRegistry();
        var writer = new ArchiveWriter(registry);
        var reader = new ArchiveReader();
        var extractor = new ArchiveExtractor(registry, reader);
        var tester = new ArchiveTester(registry, reader);

        try
        {
            var command = args[0].ToLowerInvariant();
            var remaining = args.Skip(1).ToArray();
            return command switch
            {
                "compress" => await CompressAsync(writer, remaining).ConfigureAwait(false),
                "extract" => await ExtractAsync(extractor, remaining).ConfigureAwait(false),
                "list" => List(reader, remaining),
                "info" => Info(reader, remaining),
                "test" => await TestAsync(tester, remaining).ConfigureAwait(false),
                "benchmark" => await BenchmarkAsync(writer, extractor, reader, remaining).ConfigureAwait(false),
                "open" => OpenCommand(remaining),
                "extract-here" => await ExtractHereAsync(extractor, remaining).ConfigureAwait(false),
                "extract-to-folder" => await ExtractToFolderAsync(extractor, remaining).ConfigureAwait(false),
                "extract-to-named-folder" => await ExtractToNamedFolderAsync(extractor, remaining).ConfigureAwait(false),
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

    private static async Task<int> CompressAsync(ArchiveWriter writer, string[] args)
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
            Console.Error.WriteLine("Usage: laplace compress <input_path...> <output.lpc> [options]");
            return 1;
        }

        var outputPath = positional[^1];
        var inputPaths = positional.Take(positional.Count - 1).ToArray();
        var options = ParseCreateOptions(args.Skip(optionStart).ToArray());
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"Compressing {inputPaths.Length} input path(s) -> '{outputPath}'");

        var archive = await writer.CreateAsync(inputPaths, outputPath, options, ProgressToConsole()).ConfigureAwait(false);
        stopwatch.Stop();

        var info = ArchiveInfoBuilder.Build(archive);
        Console.WriteLine();
        Console.WriteLine("Compression completed.");
        PrintSizeStats(info.OriginalSize, info.CompressedSize, stopwatch.Elapsed);

        if (options.VerifyAfterCompression)
        {
            var tester = new ArchiveTester(new CompressorRegistry());
            var testResult = await tester.TestAsync(outputPath).ConfigureAwait(false);
            Console.WriteLine(testResult.Success ? "Verification: OK" : $"Verification: FAILED ({testResult.Message})");
        }

        return 0;
    }

    private static async Task<int> ExtractAsync(ArchiveExtractor extractor, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: laplace extract <input.lpc> <output_folder> [--overwrite]");
            return 1;
        }

        var inputArchive = args[0];
        var outputFolder = args[1];
        var overwrite = args.Any(x => x.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"Extracting '{inputArchive}' -> '{outputFolder}'");

        await extractor.ExtractAsync(
            inputArchive,
            outputFolder,
            new ExtractArchiveOptions
            {
                Overwrite = overwrite,
                VerifyChecksums = true
            },
            ProgressToConsole()).ConfigureAwait(false);

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"Extraction completed in {stopwatch.Elapsed.TotalSeconds:F2}s.");
        return 0;
    }

    private static int List(ArchiveReader reader, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace list <input.lpc>");
            return 1;
        }

        var archive = reader.Read(args[0]);
        Console.WriteLine($"Archive: {args[0]}");
        Console.WriteLine("ID\tType\tOriginal\tCompressed\tMethod\tPath");
        var blockLookup = ArchiveReader.BuildBlockLookup(archive);

        foreach (var entry in archive.FileEntries.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var type = entry.IsDirectory ? "DIR" : "FILE";
            var method = "-";
            if (!entry.IsDirectory && blockLookup.TryGetValue(entry.EntryId, out var blocks))
            {
                method = string.Join(",", blocks.Select(b => b.CompressionMethod.ToString()).Distinct());
            }

            Console.WriteLine($"{entry.EntryId}\t{type}\t{entry.OriginalSize}\t{entry.CompressedSize}\t{method}\t{entry.RelativePath}");
        }

        return 0;
    }

    private static int Info(ArchiveReader reader, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace info <input.lpc>");
            return 1;
        }

        var archive = reader.Read(args[0]);
        var info = ArchiveInfoBuilder.Build(archive);
        Console.WriteLine($"Archive: {args[0]}");
        Console.WriteLine($"Version: LPC{info.ArchiveVersion}");
        Console.WriteLine($"Files: {info.FileCount}");
        Console.WriteLine($"Folders: {info.FolderCount}");
        Console.WriteLine($"Blocks: {info.BlockCount}");
        Console.WriteLine($"Created (UTC): {info.CreatedUtc:O}");
        Console.WriteLine($"Methods: {string.Join(", ", info.MethodsUsed)}");
        Console.WriteLine($"Original size: {info.OriginalSize} bytes");
        Console.WriteLine($"Compressed size: {info.CompressedSize} bytes");
        Console.WriteLine($"Ratio: {info.Ratio:P2}");
        Console.WriteLine($"Space saved: {(info.OriginalSize - info.CompressedSize)} bytes");
        return 0;
    }

    private static async Task<int> TestAsync(ArchiveTester tester, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace test <input.lpc>");
            return 1;
        }

        Console.WriteLine($"Testing archive: {args[0]}");
        var result = await tester.TestAsync(args[0], ProgressToConsole()).ConfigureAwait(false);
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
        Console.WriteLine("  laplace compress <input_path...> <output.lpc> [--mode fast|balanced|maximum|auto] [--block-size 8M] [--solid on|off|auto] [--threads N] [--verify]");
        Console.WriteLine("  laplace extract <input.lpc> <output_folder> [--overwrite]");
        Console.WriteLine("  laplace list <input.lpc>");
        Console.WriteLine("  laplace info <input.lpc>");
        Console.WriteLine("  laplace test <input.lpc>");
        Console.WriteLine("  laplace benchmark <input_path>");
        Console.WriteLine("  laplace open <archive.lpc>");
        Console.WriteLine("  laplace extract-here <archive.lpc>");
        Console.WriteLine("  laplace extract-to-folder <archive.lpc> <output_folder>");
        Console.WriteLine("  laplace extract-to-named-folder <archive.lpc>");
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

    private static string FormatSpeed(long bytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0.0001)
        {
            return "n/a";
        }

        var mbps = bytes / elapsed.TotalSeconds / (1024d * 1024d);
        return $"{mbps:F2} MB/s";
    }

    private static int OpenCommand(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace open <archive.lpc>");
            return 1;
        }

        Console.WriteLine("GUI is not part of Phase 1 build yet. Use `laplace list` or `laplace info`.");
        return 0;
    }

    private static async Task<int> ExtractHereAsync(ArchiveExtractor extractor, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace extract-here <archive.lpc>");
            return 1;
        }

        var archivePath = Path.GetFullPath(args[0]);
        var target = Path.GetDirectoryName(archivePath) ?? Directory.GetCurrentDirectory();
        await extractor.ExtractAsync(archivePath, target, new ExtractArchiveOptions { Overwrite = false, VerifyChecksums = true }).ConfigureAwait(false);
        Console.WriteLine($"Extracted to: {target}");
        return 0;
    }

    private static async Task<int> ExtractToFolderAsync(ArchiveExtractor extractor, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: laplace extract-to-folder <archive.lpc> <output_folder>");
            return 1;
        }

        await extractor.ExtractAsync(args[0], args[1], new ExtractArchiveOptions { Overwrite = false, VerifyChecksums = true }).ConfigureAwait(false);
        Console.WriteLine($"Extracted to: {args[1]}");
        return 0;
    }

    private static async Task<int> ExtractToNamedFolderAsync(ArchiveExtractor extractor, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: laplace extract-to-named-folder <archive.lpc>");
            return 1;
        }

        var archivePath = Path.GetFullPath(args[0]);
        var folder = Path.Combine(Path.GetDirectoryName(archivePath) ?? Directory.GetCurrentDirectory(), Path.GetFileNameWithoutExtension(archivePath));
        await extractor.ExtractAsync(archivePath, folder, new ExtractArchiveOptions { Overwrite = false, VerifyChecksums = true }).ConfigureAwait(false);
        Console.WriteLine($"Extracted to: {folder}");
        return 0;
    }

    private static int CompressDialogCommand(string[] args)
    {
        Console.WriteLine("GUI create-archive dialog is not available yet in this phase.");
        Console.WriteLine($"Selected arguments: {string.Join(" ", args)}");
        return 0;
    }

    private static int IntegrateCommand(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: laplace integrate install|uninstall|status [--cli-path <path>]");
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
