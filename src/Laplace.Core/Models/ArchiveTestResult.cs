namespace Laplace.Core.Models;

public sealed class ArchiveTestResult
{
    private ArchiveTestResult(bool success, string message, int fileCount, int blockCount)
    {
        Success = success;
        Message = message;
        FileCount = fileCount;
        BlockCount = blockCount;
    }

    public bool Success { get; }
    public string Message { get; }
    public int FileCount { get; }
    public int BlockCount { get; }

    public static ArchiveTestResult Ok(int fileCount, int blockCount, string message = "Archive integrity OK.")
        => new(true, message, fileCount, blockCount);

    public static ArchiveTestResult Failed(string reason) => new(false, reason, 0, 0);
}
