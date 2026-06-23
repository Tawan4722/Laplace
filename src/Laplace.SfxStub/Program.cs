using System;
using System.IO;
using System.Threading.Tasks;
using Laplace.Compression;
using Laplace.Core.Models;
using Laplace.Core.Services;

namespace Laplace.SfxStub;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine(" Laplace Self-Extracting Archive (SFX)");
        Console.WriteLine("========================================");

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath) || !File.Exists(processPath))
        {
            Console.WriteLine("Error: Could not locate the running executable path.");
            return 1;
        }

        if (!LpcSfxHelper.IsSfxFile(processPath))
        {
            Console.WriteLine("Error: This executable does not contain an embedded Laplace archive.");
            Console.WriteLine("To create an SFX, package files using Laplace CLI or GUI with .exe output extension.");
            return 1;
        }

        // Determine destination folder
        string destinationFolder;
        if (args.Length > 0)
            {
            var target = args[0];
            if (target == "-o" || target == "--output")
            {
                if (args.Length > 1)
                {
                    destinationFolder = args[1];
                }
                else
                {
                    Console.WriteLine("Error: Missing output directory after -o/--output option.");
                    return 1;
                }
            }
            else
            {
                destinationFolder = target;
            }
        }
        else
        {
            // Default to a folder next to the executable with the same name
            var fileName = Path.GetFileNameWithoutExtension(processPath);
            var dir = Path.GetDirectoryName(processPath) ?? ".";
            destinationFolder = Path.Combine(dir, fileName);
        }

        Console.WriteLine($"Source:      {processPath}");
        Console.WriteLine($"Destination: {Path.GetFullPath(destinationFolder)}");
        Console.WriteLine();

        var registry = new CompressorRegistry();
        var reader = new ArchiveReader();
        
        // Check if password is required
        PasswordContext? passwordContext = null;
        try
        {
            var header = reader.ReadHeaderOnly(processPath);
            if (header.IsEncrypted)
            {
                Console.Write("Enter archive password: ");
                var password = ReadPassword();
                Console.WriteLine();
                passwordContext = PasswordContext.FromNullable(password);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading archive header: {ex.Message}");
            return 1;
        }

        var extractor = new ArchiveExtractor(registry, reader);
        var options = new ExtractArchiveOptions
        {
            Password = passwordContext,
            Overwrite = true
        };

        var progress = new Progress<ArchiveOperationProgress>(p =>
        {
            Console.Write($"\rExtracting: {p.Percent:F1}% [{p.CurrentItem}]");
        });

        try
        {
            var result = await extractor.ExtractAsync(processPath, destinationFolder, options, progress);
            Console.WriteLine();
            Console.WriteLine("----------------------------------------");
            Console.WriteLine($"Extraction completed successfully!");
            Console.WriteLine($"Files extracted: {result.SucceededFiles}");
            if (result.FailedFiles > 0)
            {
                Console.WriteLine($"Failed files:    {result.FailedFiles}");
                foreach (var err in result.Errors)
                {
                    Console.WriteLine($" - {err.RelativePath}: {err.Reason}");
                }
                return 2;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"Error during extraction: {ex.Message}");
            return 3;
        }
    }

    private static string ReadPassword()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Remove(password.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (key.KeyChar != '\u0000')
            {
                password.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        return password.ToString();
    }
}
