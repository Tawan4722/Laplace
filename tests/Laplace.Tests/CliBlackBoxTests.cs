using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Laplace.Core.Services;
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
        Assert.Contains("extreme", result.StandardOutput);
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
    public async Task PasswordFileAndKeyfile_UnlockEncryptedLpc_AndNonLpcRejectsKeyfile()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "secret.txt");
            var passwordFile = Path.Combine(root, "password.txt");
            var keyfile = Path.Combine(root, "key.bin");
            var archivePath = Path.Combine(root, "secret.lpc");
            var extractPath = Path.Combine(root, "out");
            var zipPath = Path.Combine(root, "secret.zip");
            await File.WriteAllTextAsync(sourceFile, "two factor cli payload");
            await File.WriteAllTextAsync(passwordFile, "correct horse battery staple");
            await File.WriteAllBytesAsync(keyfile, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

            AssertSuccess(await RunLaplaceAsync("compress", sourceFile, archivePath, "--password-file", passwordFile, "--keyfile", keyfile, "--no-verify"));

            var missingKeyfile = await RunLaplaceAsync("test", archivePath, "--password-file", passwordFile);
            Assert.Equal(2, missingKeyfile.ExitCode);

            var test = await RunLaplaceAsync("test", archivePath, "--password-file", passwordFile, "--keyfile", keyfile);
            AssertSuccess(test);

            var extract = await RunLaplaceAsync("extract", archivePath, extractPath, "--password-file", passwordFile, "--keyfile", keyfile, "--overwrite");
            AssertSuccess(extract);
            Assert.Equal(
                await File.ReadAllTextAsync(sourceFile),
                await File.ReadAllTextAsync(Path.Combine(extractPath, "secret.txt")));

            var zipAttempt = await RunLaplaceAsync("compress", sourceFile, zipPath, "--password-file", passwordFile, "--keyfile", keyfile, "--no-verify");
            Assert.Equal(2, zipAttempt.ExitCode);
            Assert.Contains("Keyfiles are supported for LPC archives only", zipAttempt.StandardError);
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
    public async Task JsonMode_ListInfoTestFind_ReturnsMachineReadableOutput()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "json.txt");
            var archivePath = Path.Combine(root, "json.lpc");
            await File.WriteAllTextAsync(sourceFile, "json payload needle");

            AssertSuccess(await RunLaplaceAsync("compress", sourceFile, archivePath, "--no-verify"));

            var list = await RunLaplaceAsync("list", archivePath, "--json");
            AssertSuccess(list);
            using (var document = ParseJsonOutput(list.StandardOutput))
            {
                Assert.Equal("list", document.RootElement.GetProperty("command").GetString());
                Assert.Contains(document.RootElement.GetProperty("entries").EnumerateArray(), entry =>
                    entry.GetProperty("path").GetString() == "json.txt");
            }

            var info = await RunLaplaceAsync("info", archivePath, "--json");
            AssertSuccess(info);
            using (var document = ParseJsonOutput(info.StandardOutput))
            {
                Assert.Equal("info", document.RootElement.GetProperty("command").GetString());
                Assert.Equal("LPC", document.RootElement.GetProperty("info").GetProperty("format").GetString());
            }

            var test = await RunLaplaceAsync("test", archivePath, "--json");
            AssertSuccess(test);
            using (var document = ParseJsonOutput(test.StandardOutput))
            {
                Assert.True(document.RootElement.GetProperty("result").GetProperty("success").GetBoolean());
            }

            var find = await RunLaplaceAsync("find", archivePath, "--name", "*.txt", "--text", "needle", "--json");
            AssertSuccess(find);
            using (var document = ParseJsonOutput(find.StandardOutput))
            {
                Assert.Equal("find", document.RootElement.GetProperty("command").GetString());
                Assert.Contains(document.RootElement.GetProperty("results").EnumerateArray(), entry =>
                    entry.GetProperty("path").GetString() == "json.txt" &&
                    entry.GetProperty("textMatched").GetBoolean());
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DryRun_CompressAndDelete_DoNotCreateOrMutateArchive()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "source.txt");
            var deleteFile = Path.Combine(root, "delete-me.txt");
            var dryArchivePath = Path.Combine(root, "dry.lpc");
            var archivePath = Path.Combine(root, "payload.lpc");
            await File.WriteAllTextAsync(sourceFile, "source payload");
            await File.WriteAllTextAsync(deleteFile, "delete payload");

            var dryCompress = await RunLaplaceAsync("compress", sourceFile, dryArchivePath, "--dry-run", "--json");
            AssertSuccess(dryCompress);
            Assert.False(File.Exists(dryArchivePath));
            using (var document = ParseJsonOutput(dryCompress.StandardOutput))
            {
                Assert.True(document.RootElement.GetProperty("dryRun").GetBoolean());
                Assert.Equal(dryArchivePath, document.RootElement.GetProperty("outputPath").GetString());
            }

            AssertSuccess(await RunLaplaceAsync("compress", sourceFile, deleteFile, archivePath, "--no-verify"));

            var dryDelete = await RunLaplaceAsync("delete", archivePath, "delete-me.txt", "--dry-run", "--json");
            AssertSuccess(dryDelete);
            using (var document = ParseJsonOutput(dryDelete.StandardOutput))
            {
                Assert.Equal("delete", document.RootElement.GetProperty("command").GetString());
                Assert.True(document.RootElement.GetProperty("dryRun").GetBoolean());
            }

            var list = await RunLaplaceAsync("list", archivePath);
            AssertSuccess(list);
            Assert.Contains("delete-me.txt", list.StandardOutput);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Extract_NamePattern_AndDeleteGlobFromFile_WorkThroughCli()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceDir = Path.Combine(root, "payload");
            var nestedDir = Path.Combine(sourceDir, "nested");
            Directory.CreateDirectory(nestedDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "alpha.txt"), "alpha");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "beta.log"), "beta");
            await File.WriteAllTextAsync(Path.Combine(nestedDir, "gamma.txt"), "gamma");
            var archivePath = Path.Combine(root, "payload.lpc");
            var extractPath = Path.Combine(root, "out");
            var deleteTargets = Path.Combine(root, "delete-targets.txt");
            await File.WriteAllTextAsync(deleteTargets, "*.log");

            AssertSuccess(await RunLaplaceAsync("compress", sourceDir, archivePath, "--no-verify"));

            var extract = await RunLaplaceAsync("extract", archivePath, extractPath, "--name", "*.txt", "--overwrite");
            AssertSuccess(extract);
            Assert.True(File.Exists(Path.Combine(extractPath, "payload", "alpha.txt")));
            Assert.True(File.Exists(Path.Combine(extractPath, "payload", "nested", "gamma.txt")));
            Assert.False(File.Exists(Path.Combine(extractPath, "payload", "beta.log")));

            var delete = await RunLaplaceAsync("delete", archivePath, "--from-file", deleteTargets, "--json");
            AssertSuccess(delete);
            using (var document = ParseJsonOutput(delete.StandardOutput))
            {
                Assert.Equal("delete", document.RootElement.GetProperty("command").GetString());
                Assert.Contains(document.RootElement.GetProperty("operands").EnumerateArray(), operand =>
                    operand.GetString() == "payload/beta.log");
            }

            var list = await RunLaplaceAsync("list", archivePath);
            AssertSuccess(list);
            Assert.Contains("payload/alpha.txt", list.StandardOutput);
            Assert.Contains("payload/nested/gamma.txt", list.StandardOutput);
            Assert.DoesNotContain("payload/beta.log", list.StandardOutput);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Diff_AndMergeFromFile_WorkThroughCli()
    {
        var root = CreateTempFolder();
        try
        {
            var leftDir = Path.Combine(root, "left");
            var rightDir = Path.Combine(root, "right");
            Directory.CreateDirectory(leftDir);
            Directory.CreateDirectory(rightDir);
            var leftCommon = Path.Combine(leftDir, "common.txt");
            var leftOnly = Path.Combine(leftDir, "left-only.txt");
            var rightCommon = Path.Combine(rightDir, "common.txt");
            var rightOnly = Path.Combine(rightDir, "right-only.txt");
            await File.WriteAllTextAsync(leftCommon, "left");
            await File.WriteAllTextAsync(leftOnly, "left only");
            await File.WriteAllTextAsync(rightCommon, "right side wins");
            await File.WriteAllTextAsync(rightOnly, "right only");
            var leftArchive = Path.Combine(root, "left.lpc");
            var rightArchive = Path.Combine(root, "right.lpc");
            var mergedArchive = Path.Combine(root, "merged.lpc");
            var extractPath = Path.Combine(root, "merged-out");
            var sourceList = Path.Combine(root, "merge-sources.txt");
            await File.WriteAllTextAsync(sourceList, string.Join(Environment.NewLine, [leftArchive, rightArchive]));

            AssertSuccess(await RunLaplaceAsync("compress", leftCommon, leftOnly, leftArchive, "--no-verify"));
            AssertSuccess(await RunLaplaceAsync("compress", rightCommon, rightOnly, rightArchive, "--no-verify"));

            var diff = await RunLaplaceAsync("diff", leftArchive, rightArchive, "--json");
            AssertSuccess(diff);
            using (var document = ParseJsonOutput(diff.StandardOutput))
            {
                var changes = document.RootElement.GetProperty("changes").EnumerateArray().ToArray();
                Assert.Contains(changes, change =>
                    change.GetProperty("status").GetString() == "changed" &&
                    change.GetProperty("path").GetString() == "common.txt");
                Assert.Contains(changes, change =>
                    change.GetProperty("status").GetString() == "removed" &&
                    change.GetProperty("path").GetString() == "left-only.txt");
                Assert.Contains(changes, change =>
                    change.GetProperty("status").GetString() == "added" &&
                    change.GetProperty("path").GetString() == "right-only.txt");
            }

            var merge = await RunLaplaceAsync("merge", mergedArchive, "--from-file", sourceList, "--no-verify", "--json");
            AssertSuccess(merge);
            Assert.True(File.Exists(mergedArchive));

            var list = await RunLaplaceAsync("list", mergedArchive);
            AssertSuccess(list);
            Assert.Contains("left-only.txt", list.StandardOutput);
            Assert.Contains("right-only.txt", list.StandardOutput);
            Assert.Contains("common.txt", list.StandardOutput);

            var extract = await RunLaplaceAsync("extract", mergedArchive, extractPath, "--overwrite");
            AssertSuccess(extract);
            Assert.Equal("right side wins", await File.ReadAllTextAsync(Path.Combine(extractPath, "common.txt")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Split_ByCount_CreatesPartArchives()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceDir = Path.Combine(root, "payload");
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "one.txt"), "one");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "two.txt"), "two");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "three.txt"), "three");
            var archivePath = Path.Combine(root, "payload.lpc");
            var outputPrefix = Path.Combine(root, "parts.lpc");

            AssertSuccess(await RunLaplaceAsync("compress", sourceDir, archivePath, "--no-verify"));

            var split = await RunLaplaceAsync("split", archivePath, outputPrefix, "--count", "1", "--no-verify", "--json");
            AssertSuccess(split);
            using (var document = ParseJsonOutput(split.StandardOutput))
            {
                Assert.Equal(3, document.RootElement.GetProperty("partCount").GetInt32());
            }

            var part1 = Path.Combine(root, "parts.part001.lpc");
            var part2 = Path.Combine(root, "parts.part002.lpc");
            var part3 = Path.Combine(root, "parts.part003.lpc");
            Assert.True(File.Exists(part1));
            Assert.True(File.Exists(part2));
            Assert.True(File.Exists(part3));

            var list = await RunLaplaceAsync("list", part1, "--json");
            AssertSuccess(list);
            using (var document = ParseJsonOutput(list.StandardOutput))
            {
                Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray(), entry =>
                    !entry.GetProperty("isDirectory").GetBoolean());
            }
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
    public async Task Compress_LpcVolumeSize_Succeeds()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "payload.txt");
            var archivePath = Path.Combine(root, "payload.lpc");
            await File.WriteAllTextAsync(sourceFile, "multi-volume payload");

            var result = await RunLaplaceAsync("compress", sourceFile, archivePath, "--volume-size", "1M", "--no-verify");

            Assert.True(result.ExitCode == 0, $"ExitCode: {result.ExitCode}\nStdout: {result.StandardOutput}\nStderr: {result.StandardError}");
            Assert.True(File.Exists(archivePath + ".001"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ExtremeMode_RejectsExplicitBlockSizeBeforeCompression()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "payload.txt");
            var archivePath = Path.Combine(root, "payload.lpc");
            await File.WriteAllTextAsync(sourceFile, "extreme payload");

            var result = await RunLaplaceAsync(
                "compress",
                sourceFile,
                archivePath,
                "--mode",
                "extreme",
                "--block-size",
                "64M",
                "--no-verify");

            Assert.Equal(2, result.ExitCode);
            Assert.Contains("chooses block size automatically", result.StandardError);
            Assert.False(File.Exists(archivePath));
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

#if DEBUG
        var config = "Debug";
#else
        var config = "Release";
#endif
        var dllPath = Path.Combine(repoRoot, "src", "Laplace.Cli", "bin", config, "net8.0-windows", "laplace.dll");
        startInfo.ArgumentList.Add(dllPath);
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

    private static JsonDocument ParseJsonOutput(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("{") && !trimmed.Contains("\"progress\""))
            {
                return JsonDocument.Parse(trimmed);
            }
        }
        throw new InvalidOperationException($"No valid non-progress JSON object found in output. Full stdout:\n{stdout}");
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

    [Fact]
    public async Task Compress_WithExcludeFilter_WorkThroughCli()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), "hello");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "b.log"), "world");

            var archivePath = Path.Combine(root, "archive.lpc");
            var extractPath = Path.Combine(root, "out");

            var compress = await RunLaplaceAsync("compress", sourceDir, archivePath, "--exclude", "*.log", "--no-verify");
            AssertSuccess(compress);

            var extract = await RunLaplaceAsync("extract", archivePath, extractPath, "--overwrite");
            AssertSuccess(extract);

            Assert.True(File.Exists(Path.Combine(extractPath, "src", "a.txt")));
            Assert.False(File.Exists(Path.Combine(extractPath, "src", "b.log")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Extract_ContinueOnError_WorkThroughCli()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceDir = Path.Combine(root, "src");
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), "aaaa");
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "b.txt"), "bbbb");

            var archivePath = Path.Combine(root, "archive.lpc");
            var extractPath = Path.Combine(root, "out");

            AssertSuccess(await RunLaplaceAsync("compress", sourceDir, archivePath, "--mode", "balanced", "--solid", "off", "--no-verify"));

            var archive = new ArchiveReader().Read(archivePath);
            var bRecord = archive.FileEntries.First(x => x.RelativePath.EndsWith("b.txt"));
            var bBlock = archive.BlockEntries[(int)bRecord.FirstBlockIndex];

            using (var fs = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Position = bBlock.DataOffset + 2;
                fs.WriteByte(0xFF);
            }

            var extract = await RunLaplaceAsync("extract", archivePath, extractPath, "--continue-on-error", "--overwrite");
            Assert.Equal(3, extract.ExitCode);
            Assert.Contains("Extracted 1 files successfully. 1 files failed.", extract.StandardOutput);
            Assert.Contains("b.txt", extract.StandardOutput);
            Assert.True(File.Exists(Path.Combine(extractPath, "src", "a.txt")));

            var extractJson = await RunLaplaceAsync("extract", archivePath, extractPath, "--continue-on-error", "--overwrite", "--json");
            Assert.Equal(3, extractJson.ExitCode);
            using (var doc = ParseJsonOutput(extractJson.StandardOutput))
            {
                Assert.True(doc.RootElement.GetProperty("hasErrors").GetBoolean());
                Assert.Equal(1, doc.RootElement.GetProperty("succeededFiles").GetInt32());
                Assert.Equal(1, doc.RootElement.GetProperty("failedFiles").GetInt32());
                var errorsArray = doc.RootElement.GetProperty("errors").EnumerateArray().ToArray();
                Assert.Single(errorsArray);
                Assert.Contains("b.txt", errorsArray[0].GetProperty("relativePath").GetString());
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }
}
