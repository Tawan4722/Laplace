using System.Diagnostics;
using System.Runtime.Versioning;

namespace Laplace.Core.Services;

public sealed class RarToolCommandService
{
    private static readonly string[] ExecutableNames = ["rar.exe", "WinRAR.exe"];

    public bool IsAvailable => FindRarExecutable() is not null;

    public Task AddAsync(string archivePath, IEnumerable<string> inputPaths, CancellationToken cancellationToken = default)
    {
        return RunAsync(["a", "-r", "-o+", Path.GetFullPath(archivePath), .. inputPaths.Select(Path.GetFullPath)], cancellationToken);
    }

    public Task FreshenAsync(string archivePath, IEnumerable<string> inputPaths, CancellationToken cancellationToken = default)
    {
        return RunAsync(["f", "-r", "-o+", Path.GetFullPath(archivePath), .. inputPaths.Select(Path.GetFullPath)], cancellationToken);
    }

    public Task DeleteAsync(string archivePath, IEnumerable<string> targets, CancellationToken cancellationToken = default)
    {
        return RunAsync(["d", Path.GetFullPath(archivePath), .. targets], cancellationToken);
    }

    public Task LockAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        return RunAsync(["k", Path.GetFullPath(archivePath)], cancellationToken);
    }

    public Task RepairAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        return RunAsync(["r", Path.GetFullPath(archivePath)], cancellationToken);
    }

    public async Task SetCommentAsync(string archivePath, string comment, CancellationToken cancellationToken = default)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"laplace-rar-comment-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(tempFile, comment, cancellationToken).ConfigureAwait(false);
            await RunAsync(["c", $"-z{tempFile}", Path.GetFullPath(archivePath)], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    private static async Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var executable = FindRarExecutable()
            ?? throw new NotSupportedException("RAR mutation requires WinRAR/RAR command-line tools. Install WinRAR so rar.exe or WinRAR.exe is available.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start RAR process.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await stdout.ConfigureAwait(false) + Environment.NewLine + await stderr.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? $"RAR command failed with exit code {process.ExitCode}."
                : $"RAR command failed with exit code {process.ExitCode}: {output}");
        }
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
            // best-effort cleanup
        }
    }
}
