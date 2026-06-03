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
    public static SupportedArchiveKind DetectReadKind(string archivePath)
    {
        if (LooksLikeLpc(archivePath))
        {
            return SupportedArchiveKind.Lpc;
        }

        if (LooksLikeZip(archivePath))
        {
            return SupportedArchiveKind.Zip;
        }

        if (LooksLikeSevenZip(archivePath))
        {
            return SupportedArchiveKind.SevenZip;
        }

        if (LooksLikeRar(archivePath))
        {
            return SupportedArchiveKind.Rar;
        }

        return Path.GetExtension(archivePath).ToLowerInvariant() switch
        {
            ".lpc" => SupportedArchiveKind.Lpc,
            ".zip" => SupportedArchiveKind.Zip,
            ".7z" => SupportedArchiveKind.SevenZip,
            ".rar" => SupportedArchiveKind.Rar,
            _ => SupportedArchiveKind.External
        };
    }

    public static SupportedArchiveKind DetectWriteKind(string outputArchivePath)
    {
        var extension = Path.GetExtension(outputArchivePath);
        if (string.IsNullOrEmpty(extension) || extension.Equals(".lpc", StringComparison.OrdinalIgnoreCase))
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

    public static string SupportedReadFormats => ".lpc, .zip, .7z, .rar, .iso, .tar, .tar.gz/.tgz, .tar.bz2/.tbz2, .tar.xz/.txz, .gz, .bz2, .xz, .zst, .lzip";
    public static string SupportedWriteFormats => ".lpc, .zip, .7z, .rar";

    private static bool LooksLikeLpc(string archivePath)
    {
        Span<byte> magic = stackalloc byte[4];
        return TryReadMagic(archivePath, magic) && Encoding.ASCII.GetString(magic) == ArchiveHeader.Magic;
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
        if (!File.Exists(archivePath))
        {
            return false;
        }

        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.Read(magic) == magic.Length;
    }
}
