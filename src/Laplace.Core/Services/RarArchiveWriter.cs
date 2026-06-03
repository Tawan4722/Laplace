using Laplace.Core.Enums;
using Laplace.Core.Models;
using System.Diagnostics;
using System.Runtime.Versioning;

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
        var entries = Directory.EnumerateFileSystemEntries(stagingRoot)
            .Select(Path.GetFileName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!);
        foreach (var argument in BuildRarArguments(outputArchivePath, options, entries))
        {
            startInfo.ArgumentList.Add(argument);
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
            CompressionMode.Maximum or CompressionMode.Intensive or CompressionMode.Compressed => 5,
            _ => 3
        };
    }

    internal static IReadOnlyList<string> BuildRarArguments(
        string outputArchivePath,
        CreateArchiveOptions options,
        IEnumerable<string> entries)
    {
        var arguments = new List<string>
        {
            "a",
            "-r",
            "-o+",
            "-idq",
            "-ma5",
            $"-m{MapCompressionLevel(options.Mode)}",
            $"-mt{Math.Clamp(options.Threads, 1, 32)}"
        };

        var useSolid = options.SolidMode switch
        {
            SolidMode.On => true,
            SolidMode.Off => false,
            _ => options.Mode == CompressionMode.Compressed
        };
        arguments.Add(useSolid ? "-s" : "-s-");

        if (options.Mode == CompressionMode.Compressed)
        {
            arguments.Add("-md256m");
        }

        if (options.Password is not null)
        {
            arguments.Add($"-hp{options.Password.Password}");
        }

        arguments.Add(outputArchivePath);
        arguments.AddRange(entries);
        return arguments;
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
            foreach (var root in FindWindowsInstallLocations())
            {
                foreach (var executableName in ExecutableNames)
                {
                    var candidate = Path.Combine(root, executableName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

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

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> FindWindowsInstallLocations()
    {
        foreach (var registryRoot in new[]
        {
            Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall")
        })
        {
            using (registryRoot)
            {
                if (registryRoot is null)
                {
                    continue;
                }

                foreach (var subkeyName in registryRoot.GetSubKeyNames())
                {
                    using var subkey = registryRoot.OpenSubKey(subkeyName);
                    var displayName = subkey?.GetValue("DisplayName")?.ToString();
                    if (displayName is null || !displayName.Contains("WinRAR", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var installLocation = subkey?.GetValue("InstallLocation")?.ToString();
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        yield return installLocation;
                    }
                }
            }
        }
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
