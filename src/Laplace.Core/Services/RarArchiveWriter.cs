using Laplace.Core.Enums;
using Laplace.Core.Models;
using System.Diagnostics;

namespace Laplace.Core.Services;

public sealed class RarArchiveWriter
{
    private static readonly string[] ExecutableNames = ["rar.exe", "WinRAR.exe"];

    public async Task CreateAsync(
        IEnumerable<string> inputPaths,
        string outputArchivePath,
        CreateArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var executable = FindRarExecutable()
            ?? throw new NotSupportedException("RAR creation requires WinRAR/RAR command-line tools. Install WinRAR so rar.exe or WinRAR.exe is available, or create a .7z, .zip, or .lpc archive instead.");

        var scanned = ArchivePathScanner.Scan(inputPaths)
            .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scanned.Count == 0)
        {
            throw new InvalidOperationException("No input files were found.");
        }

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputArchivePath));
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"laplace-rar-{Guid.NewGuid():N}");
        try
        {
            var totalBytes = scanned.Where(x => !x.IsDirectory).Sum(x => new FileInfo(x.FullPath).Length);
            var copiedBytes = await StageInputsAsync(scanned, stagingRoot, totalBytes, progress, cancellationToken).ConfigureAwait(false);
            await RunRarAsync(executable, stagingRoot, Path.GetFullPath(outputArchivePath), options, cancellationToken).ConfigureAwait(false);
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

    private static async Task RunRarAsync(
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
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add("-o+");
        startInfo.ArgumentList.Add("-idq");
        startInfo.ArgumentList.Add($"-m{MapCompressionLevel(options.Mode)}");
        if (options.Password is not null)
        {
            startInfo.ArgumentList.Add($"-hp{options.Password.Password}");
        }

        startInfo.ArgumentList.Add(outputArchivePath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingRoot).Select(Path.GetFileName).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            startInfo.ArgumentList.Add(entry!);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start RAR creation process.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await stdout.ConfigureAwait(false) + Environment.NewLine + await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? $"RAR creation failed with exit code {process.ExitCode}."
                : $"RAR creation failed with exit code {process.ExitCode}: {output}");
        }
    }

    private static int MapCompressionLevel(CompressionMode mode)
    {
        return mode switch
        {
            CompressionMode.Fast => 1,
            CompressionMode.Maximum or CompressionMode.Intensive => 5,
            _ => 3
        };
    }

    private static string? FindRarExecutable()
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
                    var candidate = Path.Combine(root, "WinRAR", executableName);
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
