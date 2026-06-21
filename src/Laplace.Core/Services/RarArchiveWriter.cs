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
        if (options.VolumeSizeBytes is not null)
        {
            ArchiveVolumePathHelper.DeleteExistingVolumes(outputArchivePath);
        }

        var totalBytes = scanned.Where(x => !x.IsDirectory).Sum(x => new FileInfo(x.FullPath).Length);

        var commonParent = GetCommonParent(inputPaths, out var relativePaths);
        if (commonParent is not null)
        {
            progress?.Report(new ArchiveOperationProgress
            {
                CurrentItem = "Compressing directly...",
                ProcessedBytes = 0,
                TotalBytes = totalBytes,
                Percent = 0
            });
            await RunRarAsync(executable, commonParent, Path.GetFullPath(outputArchivePath), options, relativePaths, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ArchiveOperationProgress
            {
                CurrentItem = Path.GetFileName(outputArchivePath),
                ProcessedBytes = totalBytes,
                TotalBytes = totalBytes,
                Percent = 100
            });
            return;
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"laplace-rar-{Guid.NewGuid():N}");
        try
        {
            var copiedBytes = await StageInputsAsync(scanned, stagingRoot, totalBytes, progress, cancellationToken).ConfigureAwait(false);
            var entries = Directory.EnumerateFileSystemEntries(stagingRoot)
                .Select(Path.GetFileName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!);
            await RunRarAsync(executable, stagingRoot, Path.GetFullPath(outputArchivePath), options, entries, cancellationToken).ConfigureAwait(false);
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
        string workingDirectory,
        string outputArchivePath,
        CreateArchiveOptions options,
        IEnumerable<string> entries,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
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

        if (options.VolumeSizeBytes is { } volumeSize)
        {
            arguments.Add($"-v{volumeSize}b");
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

    private static string? GetCommonParent(IEnumerable<string> inputPaths, out List<string> relativePaths)
    {
        relativePaths = new List<string>();
        var resolvedPaths = inputPaths.Select(Path.GetFullPath).ToList();
        if (resolvedPaths.Count == 0)
        {
            return null;
        }

        var firstPath = resolvedPaths[0];
        var commonParent = Path.GetDirectoryName(firstPath);
        if (string.IsNullOrEmpty(commonParent))
        {
            return null;
        }

        for (int i = 1; i < resolvedPaths.Count; i++)
        {
            var p = resolvedPaths[i];
            while (!string.IsNullOrEmpty(commonParent) && 
                   !p.StartsWith(commonParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && 
                   !string.Equals(p, commonParent, StringComparison.OrdinalIgnoreCase))
            {
                commonParent = Path.GetDirectoryName(commonParent);
            }
        }

        if (string.IsNullOrEmpty(commonParent) || !Directory.Exists(commonParent))
        {
            return null;
        }

        var commonParentNormalized = commonParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var p in resolvedPaths)
        {
            if (string.Equals(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), commonParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                commonParent = Path.GetDirectoryName(commonParent);
                if (string.IsNullOrEmpty(commonParent))
                {
                    return null;
                }
                commonParentNormalized = commonParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                break;
            }
        }

        foreach (var p in resolvedPaths)
        {
            if (!p.StartsWith(commonParentNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var rel = Path.GetRelativePath(commonParentNormalized, p);
            relativePaths.Add(rel);
        }

        return commonParentNormalized;
    }
}
