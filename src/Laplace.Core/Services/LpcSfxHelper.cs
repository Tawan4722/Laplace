using System;
using System.IO;
using System.Text;

namespace Laplace.Core.Services;

public static class LpcSfxHelper
{
    public const string SfxSignature = "SFXLPC!!";
    public const int FooterSize = 16; // 8 bytes offset + 8 bytes signature

    private static readonly byte[] SignatureBytes = Encoding.ASCII.GetBytes(SfxSignature);

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
            
            var buffer = new byte[FooterSize];
            int read = stream.Read(buffer, 0, FooterSize);
            if (read < FooterSize)
                return false;

            // Check signature in the last 8 bytes
            for (int i = 0; i < SignatureBytes.Length; i++)
            {
                if (buffer[8 + i] != SignatureBytes[i])
                    return false;
            }

            long offset = BitConverter.ToInt64(buffer, 0);
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
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (IsSfxStream(fs))
            {
                fs.Position = fs.Length - FooterSize;
                var buffer = new byte[8];
                fs.ReadExactly(buffer, 0, 8);
                long offset = BitConverter.ToInt64(buffer, 0);
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
        var guiPath = Path.Combine(baseDir, "laplace-gui.exe");
        if (File.Exists(guiPath))
            return guiPath;

        // Try searching sibling or parent paths for debugging/dev
        var currentDir = new DirectoryInfo(baseDir);
        while (currentDir != null)
        {
            // Search for laplace-gui.exe recursively in bin directories of Laplace.Desktop
            var desktopBin = Path.Combine(currentDir.FullName, "src", "Laplace.Desktop", "bin");
            if (Directory.Exists(desktopBin))
            {
                var files = Directory.GetFiles(desktopBin, "laplace-gui.exe", SearchOption.AllDirectories);
                if (files.Length > 0)
                    return files[0];
            }
            currentDir = currentDir.Parent;
        }

        return guiPath;
    }
}
