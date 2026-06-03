using System.Diagnostics;
using Xunit;

namespace Laplace.Tests;

public sealed class CliBlackBoxTests
{
    [Fact]
    public async Task Help_PrintsUsage_AndReturnsSuccess()
    {
        var result = await RunLaplaceAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Laplace CLI", result.StandardOutput);
        Assert.Contains("laplace compress", result.StandardOutput);
        Assert.Contains("intensive", result.StandardOutput);
        Assert.Contains("compressed", result.StandardOutput);
    }

    [Fact]
    public async Task UnknownCommand_PrintsUsage_AndReturnsFailure()
    {
        var result = await RunLaplaceAsync("not-a-command");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown command: not-a-command", result.StandardError);
        Assert.Contains("Laplace CLI", result.StandardOutput);
    }

    [Fact]
    public async Task CompressListInfoTestExtract_Lpc_RoundTripsThroughCli()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "hello.txt");
            await File.WriteAllTextAsync(sourceFile, string.Join(Environment.NewLine, Enumerable.Repeat("Laplace CLI round trip", 200)));
            var archivePath = Path.Combine(root, "hello.lpc");
            var extractPath = Path.Combine(root, "out");

            var compress = await RunLaplaceAsync("compress", sourceFile, archivePath, "--mode", "balanced", "--block-size", "4M", "--no-verify");
            AssertSuccess(compress);
            Assert.True(File.Exists(archivePath));
            Assert.Contains("Compression completed.", compress.StandardOutput);

            var list = await RunLaplaceAsync("list", archivePath);
            AssertSuccess(list);
            Assert.Contains("hello.txt", list.StandardOutput);

            var info = await RunLaplaceAsync("info", archivePath);
            AssertSuccess(info);
            Assert.Contains("Format: LPC", info.StandardOutput);
            Assert.Contains("Encrypted: False", info.StandardOutput);

            var test = await RunLaplaceAsync("test", archivePath);
            AssertSuccess(test);
            Assert.Contains("Integrity OK", test.StandardOutput);

            var extract = await RunLaplaceAsync("extract", archivePath, extractPath, "--overwrite");
            AssertSuccess(extract);
            Assert.Equal(
                await File.ReadAllTextAsync(sourceFile),
                await File.ReadAllTextAsync(Path.Combine(extractPath, "hello.txt")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Extract_NoVerify_RoundTripsThroughCli()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "fast.txt");
            await File.WriteAllTextAsync(sourceFile, string.Join(Environment.NewLine, Enumerable.Repeat("fast extraction path", 200)));
            var archivePath = Path.Combine(root, "fast.lpc");
            var extractPath = Path.Combine(root, "out");

            AssertSuccess(await RunLaplaceAsync("compress", sourceFile, archivePath, "--mode", "fast", "--no-verify"));
            var extract = await RunLaplaceAsync("extract", archivePath, extractPath, "--overwrite", "--no-verify");

            AssertSuccess(extract);
            Assert.Equal(
                await File.ReadAllTextAsync(sourceFile),
                await File.ReadAllTextAsync(Path.Combine(extractPath, "fast.txt")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PasswordFile_UnlocksEncryptedLpc_AndMissingPasswordFailsNonInteractively()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "secret.txt");
            var passwordFile = Path.Combine(root, "password.txt");
            var archivePath = Path.Combine(root, "secret.lpc");
            var extractPath = Path.Combine(root, "out");
            await File.WriteAllTextAsync(sourceFile, "encrypted CLI payload");
            await File.WriteAllTextAsync(passwordFile, "correct horse battery staple");

            var compress = await RunLaplaceAsync("compress", sourceFile, archivePath, "--password-file", passwordFile, "--no-verify");
            AssertSuccess(compress);

            var missingPassword = await RunLaplaceAsync("test", archivePath);
            Assert.Equal(2, missingPassword.ExitCode);
            Assert.Contains("requires a password", missingPassword.StandardError, StringComparison.OrdinalIgnoreCase);

            var test = await RunLaplaceAsync("test", archivePath, "--password-file", passwordFile);
            AssertSuccess(test);
            Assert.Contains("Integrity OK", test.StandardOutput);

            var extract = await RunLaplaceAsync("extract", archivePath, extractPath, "--password-file", passwordFile, "--overwrite");
            AssertSuccess(extract);
            Assert.Equal(
                await File.ReadAllTextAsync(sourceFile),
                await File.ReadAllTextAsync(Path.Combine(extractPath, "secret.txt")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task MutationCommands_AddCommentFindDelete_Lpc_WorkThroughCli()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "base.txt");
            var addedFile = Path.Combine(root, "added.txt");
            var archivePath = Path.Combine(root, "mut.lpc");
            await File.WriteAllTextAsync(sourceFile, "base payload");
            await File.WriteAllTextAsync(addedFile, "added payload needle");

            AssertSuccess(await RunLaplaceAsync("compress", sourceFile, archivePath, "--no-verify"));
            AssertSuccess(await RunLaplaceAsync("add", archivePath, addedFile));
            AssertSuccess(await RunLaplaceAsync("comment", archivePath, "--set", "cli mutation"));

            var info = await RunLaplaceAsync("info", archivePath);
            AssertSuccess(info);
            Assert.Contains("Comment: cli mutation", info.StandardOutput);

            var find = await RunLaplaceAsync("find", archivePath, "--name", "*.txt", "--text", "needle");
            AssertSuccess(find);
            Assert.Contains("added.txt", find.StandardOutput);

            AssertSuccess(await RunLaplaceAsync("delete", archivePath, "base.txt"));
            var list = await RunLaplaceAsync("list", archivePath);
            AssertSuccess(list);
            Assert.DoesNotContain("base.txt", list.StandardOutput);
            Assert.Contains("added.txt", list.StandardOutput);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Compress_SingleFileWithoutOutput_CreatesLpcBesideInput()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "report.pdf");
            var archivePath = Path.Combine(root, "report.lpc");
            await File.WriteAllTextAsync(sourceFile, string.Concat(Enumerable.Repeat("auto name ", 100)));

            var compress = await RunLaplaceAsync("compress", sourceFile, "--mode", "balanced", "--no-verify");

            AssertSuccess(compress);
            Assert.True(File.Exists(archivePath));
            Assert.Contains($"-> '{archivePath}'", compress.StandardOutput);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Compress_SingleFolderWithoutOutput_CreatesLpcBesideInput()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFolder = Path.Combine(root, "Photos");
            Directory.CreateDirectory(sourceFolder);
            await File.WriteAllTextAsync(Path.Combine(sourceFolder, "image.txt"), string.Concat(Enumerable.Repeat("folder auto name ", 100)));
            var archivePath = Path.Combine(root, "Photos.lpc");

            var compress = await RunLaplaceAsync("compress", sourceFolder, "--mode", "balanced", "--no-verify");

            AssertSuccess(compress);
            Assert.True(File.Exists(archivePath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Compress_SingleFileWithoutOutput_UsesNumberedNameWhenArchiveExists()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "report.pdf");
            var existingArchive = Path.Combine(root, "report.lpc");
            var archivePath = Path.Combine(root, "report (2).lpc");
            await File.WriteAllTextAsync(sourceFile, string.Concat(Enumerable.Repeat("auto name collision ", 100)));
            await File.WriteAllTextAsync(existingArchive, "existing archive");

            var compress = await RunLaplaceAsync("compress", sourceFile, "--mode", "balanced", "--no-verify");

            AssertSuccess(compress);
            Assert.True(File.Exists(existingArchive));
            Assert.True(File.Exists(archivePath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Compress_MultipleInputsWithoutOutput_ReturnsClearFailure()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFileA = Path.Combine(root, "a.txt");
            var sourceFileB = Path.Combine(root, "b.txt");
            await File.WriteAllTextAsync(sourceFileA, "alpha");
            await File.WriteAllTextAsync(sourceFileB, "beta");

            var result = await RunLaplaceAsync("compress", sourceFileA, sourceFileB, "--no-verify");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Multiple input paths require an explicit output archive path", result.StandardError);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task InvalidArguments_ReturnFailure_WithoutCreatingArchive()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "data.txt");
            var archivePath = Path.Combine(root, "bad.lpc");
            await File.WriteAllTextAsync(sourceFile, "data");

            var result = await RunLaplaceAsync("compress", sourceFile, archivePath, "--block-size", "5M");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("Block size must be one of", result.StandardError);
            Assert.False(File.Exists(archivePath));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task<CliResult> RunLaplaceAsync(params string[] arguments)
    {
        var repoRoot = FindRepoRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(repoRoot, "src", "Laplace.Cli", "Laplace.Cli.csproj"));
        startInfo.ArgumentList.Add("--");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Laplace CLI.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort cleanup after a hung CLI process
            }

            throw new TimeoutException($"Laplace CLI timed out: {string.Join(" ", arguments)}");
        }

        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    private static void AssertSuccess(CliResult result)
    {
        Assert.True(
            result.ExitCode == 0,
            $"Expected exit code 0, got {result.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Laplace.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing Laplace.sln.");
    }

    private static string CreateTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"laplace-cli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
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

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
