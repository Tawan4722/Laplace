using Laplace.Core.Models;
using System.Text;

namespace Laplace.Core.Services;

public enum SupportedArchiveKind
{
    Lpc,
    Zip,
    External
}

public static class ArchiveFormatDetector
{
    public static SupportedArchiveKind DetectReadKind(string archivePath)
    {
        if (LooksLikeLpc(archivePath) || Path.GetExtension(archivePath).Equals(".lpc", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedArchiveKind.Lpc;
        }

        if (LooksLikeZip(archivePath) || Path.GetExtension(archivePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedArchiveKind.Zip;
        }

        return SupportedArchiveKind.External;
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

        throw new NotSupportedException($"Unsupported output archive format '{extension}'. Supported write formats: .lpc, .zip");
    }

    public static string SupportedReadFormats => ".lpc, .zip, .7z, .rar, .tar, .tar.gz/.tgz, .tar.bz2/.tbz2, .tar.xz/.txz, .gz, .bz2, .xz, .zst, .lzip";

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
