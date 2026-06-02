using Laplace.Core.Abstractions;
using Laplace.Core.Enums;
using System.Diagnostics;

namespace Laplace.Compression.Compressors;

public sealed class ExternalCommandCompressor : IBlockCompressor
{
    private readonly string _compressCommand;
    private readonly string _decompressCommand;

    private ExternalCommandCompressor(CompressionMethod method, string compressCommand, string decompressCommand)
    {
        Method = method;
        _compressCommand = compressCommand;
        _decompressCommand = decompressCommand;
    }

    public CompressionMethod Method { get; }
    public int Level => 0;

    public static ExternalCommandCompressor? TryCreate(
        CompressionMethod method,
        string compressEnvironmentVariable,
        string decompressEnvironmentVariable)
    {
        var compressCommand = Environment.GetEnvironmentVariable(compressEnvironmentVariable);
        var decompressCommand = Environment.GetEnvironmentVariable(decompressEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(compressCommand) || string.IsNullOrWhiteSpace(decompressCommand))
        {
            return null;
        }

        return new ExternalCommandCompressor(method, compressCommand, decompressCommand);
    }

    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        return RunCommand(_compressCommand, data.ToArray(), expectedOutputSize: null);
    }

    public byte[] Decompress(ReadOnlySpan<byte> data, int expectedDecompressedSize)
    {
        return RunCommand(_decompressCommand, data.ToArray(), expectedDecompressedSize);
    }

    private byte[] RunCommand(string commandTemplate, byte[] inputBytes, int? expectedOutputSize)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"laplace-codec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var inputPath = Path.Combine(tempRoot, "input.bin");
            var outputPath = Path.Combine(tempRoot, "output.bin");
            File.WriteAllBytes(inputPath, inputBytes);

            var command = commandTemplate
                .Replace("{input}", Quote(inputPath), StringComparison.Ordinal)
                .Replace("{output}", Quote(outputPath), StringComparison.Ordinal);
            RunShellCommand(command, tempRoot);

            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException($"{Method} command did not create an output file.");
            }

            var output = File.ReadAllBytes(outputPath);
            if (expectedOutputSize is not null && output.Length != expectedOutputSize.Value)
            {
                throw new InvalidDataException($"{Method} decompressed {output.Length} bytes; expected {expectedOutputSize.Value}.");
            }

            return output;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void RunShellCommand(string command, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh"
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/S");
            startInfo.ArgumentList.Add("/C");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        startInfo.WorkingDirectory = workingDirectory;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.CreateNoWindow = true;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start external codec command.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var output = string.Join(Environment.NewLine, stdout, stderr).Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? $"External codec command failed with exit code {process.ExitCode}."
                : $"External codec command failed with exit code {process.ExitCode}: {output}");
        }
    }

    private static string Quote(string value)
    {
        if (OperatingSystem.IsWindows())
        {
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup for external codec temporary files.
        }
    }
}
