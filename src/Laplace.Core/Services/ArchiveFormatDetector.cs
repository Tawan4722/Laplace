using Laplace.Core.Models;
using System.Text;

namespace Laplace.Core.Services;

public enum SupportedArchiveKind
{
    Lpc,
    Zip,
    SevenZip,
    Rar,
    External
}

public static class ArchiveFormatDetector
{
    public static bool IsUrl(string path)
    {
        return path != null &&
               (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    public static SupportedArchiveKind DetectReadKind(string archivePath)
    {
        if (IsUrl(archivePath))
        {
            return SupportedArchiveKind.Lpc;
        }

        var resolvedPath = archivePath;
        if (!File.Exists(resolvedPath))
        {
            if (MultiVolumeStream.IsMultiVolumeFirstFile(archivePath, out var firstVolPath))
            {
                resolvedPath = firstVolPath;
            }
        }


        if (LooksLikeLpc(resolvedPath) || LpcSfxHelper.IsSfxFile(resolvedPath))
        {
            return SupportedArchiveKind.Lpc;
        }

        if (LooksLikeZip(resolvedPath))
        {
            return SupportedArchiveKind.Zip;
        }

        if (LooksLikeSevenZip(resolvedPath))
        {
            return SupportedArchiveKind.SevenZip;
        }

        if (LooksLikeRar(resolvedPath))
        {
            return SupportedArchiveKind.Rar;
        }

        var ext = Path.GetExtension(archivePath).ToLowerInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(ext, @"^\.\d{3}$"))
        {
            var withoutVolExt = Path.GetFileNameWithoutExtension(archivePath);
            ext = Path.GetExtension(withoutVolExt).ToLowerInvariant();
        }

        return ext switch
        {
            ".lpc" => SupportedArchiveKind.Lpc,
            ".zip" => SupportedArchiveKind.Zip,
            ".7z" => SupportedArchiveKind.SevenZip,
            ".rar" => SupportedArchiveKind.Rar,
            ".cab" => SupportedArchiveKind.External,
            _ => SupportedArchiveKind.External
        };
    }

    public static SupportedArchiveKind DetectWriteKind(string outputArchivePath)
    {
        var extension = Path.GetExtension(outputArchivePath);
        if (string.IsNullOrEmpty(extension) || 
            extension.Equals(".lpc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedArchiveKind.Lpc;
        }

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedArchiveKind.Zip;
        }

        if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedArchiveKind.SevenZip;
        }

        if (extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedArchiveKind.Rar;
        }

        throw new NotSupportedException($"Unsupported output archive format '{extension}'. Supported write formats: {SupportedWriteFormats}");
    }

    public static string SupportedReadFormats => ".lpc, .zip, .7z, .rar, .cab, .iso, .tar, .tar.gz/.tgz, .tar.bz2/.tbz2, .tar.xz/.txz, .gz, .bz2, .xz, .zst, .lzip";
    public static string SupportedWriteFormats => ".lpc, .zip, .7z, .rar";

    private static bool LooksLikeLpc(string archivePath)
    {
        Span<byte> magic = stackalloc byte[4];
        return TryReadMagic(archivePath, magic) && magic.SequenceEqual("LPC1"u8);
    }

    private static bool LooksLikeZip(string archivePath)
    {
        Span<byte> magic = stackalloc byte[4];
        return TryReadMagic(archivePath, magic) &&
               magic[0] == 0x50 &&
               magic[1] == 0x4B &&
               (magic[2] is 0x03 or 0x05 or 0x07) &&
               (magic[3] is 0x04 or 0x06 or 0x08);
    }

    private static bool LooksLikeSevenZip(string archivePath)
    {
        Span<byte> magic = stackalloc byte[6];
        return TryReadMagic(archivePath, magic) &&
               magic[0] == 0x37 &&
               magic[1] == 0x7A &&
               magic[2] == 0xBC &&
               magic[3] == 0xAF &&
               magic[4] == 0x27 &&
               magic[5] == 0x1C;
    }

    private static bool LooksLikeRar(string archivePath)
    {
        Span<byte> magic = stackalloc byte[8];
        if (!TryReadMagic(archivePath, magic))
        {
            return false;
        }

        var hasRarPrefix = magic[0] == 0x52 &&
                           magic[1] == 0x61 &&
                           magic[2] == 0x72 &&
                           magic[3] == 0x21 &&
                           magic[4] == 0x1A &&
                           magic[5] == 0x07;
        return hasRarPrefix && magic[6] is 0x00 or 0x01;
    }

    private static bool TryReadMagic(string archivePath, Span<byte> magic)
    {
        var resolvedPath = archivePath;
        if (!File.Exists(resolvedPath))
        {
            if (MultiVolumeStream.IsMultiVolumeFirstFile(archivePath, out var firstVolPath))
            {
                resolvedPath = firstVolPath;
            }
            else
            {
                return false;
            }
        }

        try
        {
            using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Read(magic) == magic.Length;
        }
        catch
        {
            return false;
        }
    }
}
