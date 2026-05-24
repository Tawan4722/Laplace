namespace Laplace.Core.Exceptions;

public sealed class ArchivePasswordRequiredException : LaplaceArchiveException
{
    public ArchivePasswordRequiredException(string archivePath)
        : base($"Archive requires a password: {archivePath}")
    {
        ArchivePath = archivePath;
    }

    public string ArchivePath { get; }
}
