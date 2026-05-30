using Laplace.Core.Models;
using Laplace.Core.Security;
using System.Diagnostics;

namespace Laplace.Core.Services;

public sealed class WindowsNativeArchiveHandler
{
    public bool IsAvailable => OperatingSystem.IsWindows() && ResolveTarExecutable() is not null;

    public async Task ExtractAsync(
        string archivePath,
        string destinationFolder,
        ExtractArchiveOptions options,
        IProgress<ArchiveOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows native archive extraction is only available on Windows.");
        }

        if (options.Password is not null)
        {
            throw new NotSupportedException("Windows native archive extraction does not support password-protected archives.");
        }

        var tarPath = ResolveTarExecutable()
            ?? throw new NotSupportedException("Windows native archive extraction requires tar.exe.");

        var entries = await ListEntriesAsync(tarPath, archivePath, cancellationToken).ConfigureAwait(false);
        ValidateEntryPaths(entries, destinationFolder);

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"laplace-native-extract-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingRoot);
            await RunTarAsync(
                tarPath,
                ["-xf", Path.GetFullPath(archivePath), "-C", stagingRoot],
                cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(destinationFolder);
            await CopyStagedOutputAsync(stagingRoot, destinationFolder, options.Overwrite, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static async Task<string[]> ListEntriesAsync(string tarPath, string archivePath, CancellationToken cancellationToken)
    {
        var result = await RunTarAsync(
            tarPath,
            ["-tf", Path.GetFullPath(archivePath)],
            cancellationToken).ConfigureAwait(false);

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static void ValidateEntryPaths(IEnumerable<string> entries, string destinationFolder)
    {
        foreach (var entry in entries)
        {
            PathSecurity.EnsureSafeExtractionPath(destinationFolder, entry);
        }
    }

    private static async Task CopyStagedOutputAsync(
        string stagingRoot,
        string destinationFolder,
        bool overwrite,
        IProgress<ArchiveOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories).ToList();
        var totalBytes = files.Sum(path => new FileInfo(path).Length);
        long processedBytes = 0;

        foreach (var directory in Directory.EnumerateDirectories(stagingRoot, "*", SearchOption.AllDirectories).OrderBy(x => x.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(directory);
            var relative = Path.GetRelativePath(stagingRoot, directory);
            var destination = PathSecurity.EnsureSafeExtractionPath(destinationFolder, relative);
            if (File.Exists(destination))
            {
                throw new IOException($"File already exists where a directory is required: {destination}");
            }

            PathSecurity.EnsureNoReparsePointInPath(destinationFolder, destination);
            Directory.CreateDirectory(destination);
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(file);
            var relative = Path.GetRelativePath(stagingRoot, file);
            var destination = PathSecurity.EnsureSafeExtractionPath(destinationFolder, relative);
            if (!overwrite && File.Exists(destination))
            {
                throw new IOException($"File already exists: {destination}. Use overwrite mode to replace.");
            }

            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                PathSecurity.EnsureNoReparsePointInPath(destinationFolder, parent);
                Directory.CreateDirectory(parent);
            }

            PathSecurity.EnsureNoReparsePointInPath(destinationFolder, destination);
            await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                processedBytes += input.Length;
            }

            File.SetLastWriteTime(destination, File.GetLastWriteTime(file));
            progress?.Report(new ArchiveOperationProgress
            {
                CurrentItem = relative,
                ProcessedBytes = processedBytes,
                TotalBytes = totalBytes,
                Percent = totalBytes == 0 ? 100 : (double)processedBytes / totalBytes * 100d
            });
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"Archive links are not extracted for safety: {path}");
        }
    }

    private static async Task<ProcessResult> RunTarAsync(string tarPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tarPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Windows native archive extraction.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var result = new ProcessResult(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), process.ExitCode);
        if (result.ExitCode != 0)
        {
            var output = string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            throw new NotSupportedException(string.IsNullOrWhiteSpace(output)
                ? $"Windows native archive extraction failed with exit code {result.ExitCode}."
                : $"Windows native archive extraction failed with exit code {result.ExitCode}: {output}");
        }

        return result;
    }

    private static string? ResolveTarExecutable()
    {
        var systemDirectory = Environment.SystemDirectory;
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            var systemTar = Path.Combine(systemDirectory, "tar.exe");
            if (File.Exists(systemTar))
            {
                return systemTar;
            }
        }

        return FindOnPath("tar.exe");
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

    private sealed record ProcessResult(string StandardOutput, string StandardError, int ExitCode);
}
