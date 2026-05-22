namespace Laplace.Core.Compression;

public static class FileTypeDetector
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tiff", ".heic", ".ico"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".aac", ".flac", ".wav", ".ogg", ".m4a", ".opus"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".gz", ".xz", ".zst", ".bz2", ".tar", ".cab", ".iso", ".lpc"
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".sys", ".bin"
    };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".cpp", ".c", ".h", ".hpp", ".java", ".kt", ".go", ".rs", ".py", ".js", ".ts", ".tsx", ".jsx",
        ".html", ".css", ".json", ".xml", ".yaml", ".yml", ".toml", ".md", ".sql", ".sh", ".ps1"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf"
    };

    private static readonly HashSet<string> DatabaseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb"
    };

    private static readonly HashSet<string> LogExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".trace"
    };

    public static FileTypeCategory Detect(string path, ReadOnlySpan<byte> magicSample)
    {
        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension)) return FileTypeCategory.Image;
        if (VideoExtensions.Contains(extension)) return FileTypeCategory.Video;
        if (AudioExtensions.Contains(extension)) return FileTypeCategory.Audio;
        if (ArchiveExtensions.Contains(extension)) return FileTypeCategory.Archive;
        if (ExecutableExtensions.Contains(extension)) return FileTypeCategory.Executable;
        if (DatabaseExtensions.Contains(extension)) return FileTypeCategory.Database;
        if (LogExtensions.Contains(extension)) return FileTypeCategory.Log;
        if (DocumentExtensions.Contains(extension)) return FileTypeCategory.Document;
        if (SourceExtensions.Contains(extension)) return FileTypeCategory.SourceCode;

        if (LooksLikeZip(magicSample) || LooksLikeGzip(magicSample) || LooksLike7z(magicSample) || LooksLikePdf(magicSample))
        {
            return FileTypeCategory.Archive;
        }

        if (LooksLikePng(magicSample) || LooksLikeJpeg(magicSample) || LooksLikeWebp(magicSample))
        {
            return FileTypeCategory.Image;
        }

        if (LooksLikeElf(magicSample) || LooksLikePe(magicSample))
        {
            return FileTypeCategory.Executable;
        }

        if (IsLikelyText(magicSample))
        {
            return FileTypeCategory.TextLike;
        }

        return FileTypeCategory.Binary;
    }

    private static bool IsLikelyText(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return false;
        var printable = 0;
        foreach (var b in data)
        {
            if (b is 9 or 10 or 13 || (b >= 32 && b <= 126))
            {
                printable++;
            }
        }

        return (double)printable / data.Length >= 0.9;
    }

    private static bool LooksLikeZip(ReadOnlySpan<byte> s) => s.Length >= 4 && s[0] == 0x50 && s[1] == 0x4B && s[2] == 0x03 && s[3] == 0x04;
    private static bool LooksLikeGzip(ReadOnlySpan<byte> s) => s.Length >= 2 && s[0] == 0x1F && s[1] == 0x8B;
    private static bool LooksLike7z(ReadOnlySpan<byte> s) => s.Length >= 6 && s[0] == 0x37 && s[1] == 0x7A && s[2] == 0xBC && s[3] == 0xAF && s[4] == 0x27 && s[5] == 0x1C;
    private static bool LooksLikePdf(ReadOnlySpan<byte> s) => s.Length >= 4 && s[0] == 0x25 && s[1] == 0x50 && s[2] == 0x44 && s[3] == 0x46;
    private static bool LooksLikePng(ReadOnlySpan<byte> s) => s.Length >= 8 && s[0] == 0x89 && s[1] == 0x50 && s[2] == 0x4E && s[3] == 0x47;
    private static bool LooksLikeJpeg(ReadOnlySpan<byte> s) => s.Length >= 3 && s[0] == 0xFF && s[1] == 0xD8 && s[2] == 0xFF;
    private static bool LooksLikeWebp(ReadOnlySpan<byte> s) => s.Length >= 12 && s[0] == 0x52 && s[1] == 0x49 && s[2] == 0x46 && s[3] == 0x46 && s[8] == 0x57 && s[9] == 0x45 && s[10] == 0x42 && s[11] == 0x50;
    private static bool LooksLikeElf(ReadOnlySpan<byte> s) => s.Length >= 4 && s[0] == 0x7F && s[1] == 0x45 && s[2] == 0x4C && s[3] == 0x46;
    private static bool LooksLikePe(ReadOnlySpan<byte> s) => s.Length >= 2 && s[0] == 0x4D && s[1] == 0x5A;
}
