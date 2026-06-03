using Laplace.Core.Enums;
using Laplace.Core.Models;
using Laplace.Core.Security;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using System.Diagnostics;

namespace Laplace.Core.Services;

public sealed class SevenZipArchiveWriter
{
    private static readonly string[] ExecutableNames = ["7z.exe", "7za.exe"];

    public async Task CreateAsync(
        IEnumerable<string> inputPaths,
        string outputArchivePath,
        CreateArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scanned = ArchivePathScanner.Scan(inputPaths)
            .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scanned.Count == 0)
        {
            throw new InvalidOperationException("No input files were found.");
        }

        if (ShouldUseExternalSevenZip(options) && FindSevenZipExecutable() is { } executable)
        {
            await CreateExternalAsync(executable, scanned, outputArchivePath, options, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (options.Password is not null)
        {
            throw new NotSupportedException("Encrypted 7z creation requires installed 7-Zip command-line tools. Install 7-Zip or create an encrypted .lpc/.zip archive instead.");
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
            CompressionMode.Maximum or CompressionMode.Intensive or CompressionMode.Compressed => 9,
            _ => 6
        };
    }

    private static bool ShouldUseExternalSevenZip(CreateArchiveOptions options)
    {
        return options.Password is not null ||
            options.Mode is CompressionMode.Maximum or CompressionMode.Intensive or CompressionMode.Compressed ||
            options.SolidMode == SolidMode.On;
    }

    private static async Task CreateExternalAsync(
        string executable,
        IReadOnlyList<InputEntry> scanned,
        string outputArchivePath,
        CreateArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputArchivePath));
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"laplace-7z-{Guid.NewGuid():N}");
        try
        {
            var totalBytes = scanned.Where(x => !x.IsDirectory).Sum(x => new FileInfo(x.FullPath).Length);
            var copiedBytes = await StageInputsAsync(scanned, stagingRoot, totalBytes, progress, cancellationToken).ConfigureAwait(false);
            await RunSevenZipAsync(executable, stagingRoot, Path.GetFullPath(outputArchivePath), options, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ArchiveOperationProgress
            {
                CurrentItem = Path.GetFileName(outputArchivePath),
                ProcessedBytes = copiedBytes,
                TotalBytes = totalBytes,
                Percent = 100
            });
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static async Task<long> StageInputsAsync(
        IReadOnlyList<InputEntry> scanned,
        string stagingRoot,
        long totalBytes,
        IProgress<ArchiveOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingRoot);
        long copiedBytes = 0;
        foreach (var source in scanned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(stagingRoot, source.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (source.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await using (var input = new FileStream(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                copiedBytes += input.Length;
            }

            File.SetLastWriteTime(destination, File.GetLastWriteTime(source.FullPath));
            progress?.Report(new ArchiveOperationProgress
            {
                CurrentItem = source.RelativePath,
                ProcessedBytes = copiedBytes,
                TotalBytes = totalBytes,
                Percent = totalBytes == 0 ? 100 : (double)copiedBytes / totalBytes * 100d
            });
        }

        return copiedBytes;
    }

    private static async Task RunSevenZipAsync(
        string executable,
        string stagingRoot,
        string outputArchivePath,
        CreateArchiveOptions options,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = stagingRoot,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("a");
        startInfo.ArgumentList.Add("-t7z");
        startInfo.ArgumentList.Add($"-mx={MapCompressionLevel(options.Mode)}");
        startInfo.ArgumentList.Add("-m0=lzma2");
        startInfo.ArgumentList.Add($"-mmt={Math.Max(1, options.Threads)}");
        startInfo.ArgumentList.Add(GetSolidSwitch(options));
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-bd");
        if (options.Mode == CompressionMode.Compressed)
        {
            startInfo.ArgumentList.Add("-mfb=273");
            startInfo.ArgumentList.Add("-md=256m");
        }

        if (options.Password is not null)
        {
            startInfo.ArgumentList.Add($"-p{options.Password.Password}");
            startInfo.ArgumentList.Add("-mhe=on");
        }

        startInfo.ArgumentList.Add(outputArchivePath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingRoot).Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            startInfo.ArgumentList.Add(entry!);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start 7-Zip creation process.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await stdout.ConfigureAwait(false) + Environment.NewLine + await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? $"7-Zip creation failed with exit code {process.ExitCode}."
                : $"7-Zip creation failed with exit code {process.ExitCode}: {output}");
        }
    }

    private static string GetSolidSwitch(CreateArchiveOptions options)
    {
        return options.SolidMode switch
        {
            SolidMode.Off => "-ms=off",
            SolidMode.On => "-ms=on",
            _ => options.Mode is CompressionMode.Maximum or CompressionMode.Intensive or CompressionMode.Compressed ? "-ms=on" : "-ms=off"
        };
    }

    private static string? FindSevenZipExecutable()
    {
        foreach (var executableName in ExecutableNames)
        {
            var pathMatch = FindOnPath(executableName);
            if (pathMatch is not null)
            {
                return pathMatch;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (var root in programFiles.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var executableName in ExecutableNames)
                {
                    var candidate = Path.Combine(root, "7-Zip", executableName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return null;
    }

    private static string? FindOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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
            // best-effort cleanup
        }
    }
}
