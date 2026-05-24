namespace Laplace.Core.Exceptions;

public sealed class ArchivePasswordException : LaplaceArchiveException
{
    public ArchivePasswordException(string message) : base(message)
    {
    }
}
