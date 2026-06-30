using System;
using System.IO;
using System.Text;

namespace Laplace.Core.Services;

public static class LpcSfxHelper
{
    public const string SfxSignature = "SFXLPC!!";
    public const int FooterSize = 16; // 8 bytes offset + 8 bytes signature



    public static bool IsRunningAsSfx
    {
        get
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(processPath)) return false;
                return IsSfxFile(processPath);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsSfxFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return IsSfxStream(fs);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSfxStream(Stream stream)
    {
        if (stream.Length < FooterSize)
            return false;

        var originalPosition = stream.Position;
        try
        {
            stream.Position = stream.Length - FooterSize;
            
            Span<byte> buffer = stackalloc byte[FooterSize];
            stream.ReadExactly(buffer);

            // Check signature in the last 8 bytes
            if (!buffer[8..].SequenceEqual("SFXLPC!!"u8))
                return false;

            long offset = BitConverter.ToInt64(buffer);
            return offset > 0 && offset < stream.Length - FooterSize;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    public static Stream OpenArchiveStream(string path)
    {
        if (ArchiveFormatDetector.IsUrl(path))
        {
            return new HttpRangeStream(path);
        }

        if (MultiVolumeStream.IsMultiVolumeFirstFile(path, out string firstVolPath))
        {
            return new MultiVolumeStream(firstVolPath);
        }

        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (IsSfxStream(fs))
            {
                fs.Position = fs.Length - FooterSize;
                Span<byte> buffer = stackalloc byte[8];
                fs.ReadExactly(buffer);
                long offset = BitConverter.ToInt64(buffer);
                long length = fs.Length - offset - FooterSize;
                return new SubStream(fs, offset, length);
            }
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public static string GetSfxStubPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var stubName = "laplace-sfx-stub.exe";
        var stubPath = Path.Combine(baseDir, stubName);
        if (File.Exists(stubPath))
            return stubPath;

        // Try searching sibling or parent paths for debugging/dev
        var currentDir = new DirectoryInfo(baseDir);
        while (currentDir != null)
        {
            // Search for laplace-sfx-stub.exe recursively in bin directories of Laplace.SfxStub
            var stubBin = Path.Combine(currentDir.FullName, "src", "Laplace.SfxStub", "bin");
            if (Directory.Exists(stubBin))
            {
                var files = Directory.GetFiles(stubBin, stubName, SearchOption.AllDirectories);
                if (files.Length > 0)
                    return files[0];
            }
            // Search for laplace-gui.exe recursively in bin directories of Laplace.Desktop as fallback
            var desktopBin = Path.Combine(currentDir.FullName, "src", "Laplace.Desktop", "bin");
            if (Directory.Exists(desktopBin))
            {
                var files = Directory.GetFiles(desktopBin, "laplace-gui.exe", SearchOption.AllDirectories);
                if (files.Length > 0)
                    return files[0];
            }
            currentDir = currentDir.Parent;
        }

        // Final fallback: check for laplace-gui.exe next to executable
        var guiPath = Path.Combine(baseDir, "laplace-gui.exe");
        if (File.Exists(guiPath))
            return guiPath;

        return stubPath;
    }
}
