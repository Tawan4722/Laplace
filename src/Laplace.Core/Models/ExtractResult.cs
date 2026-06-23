namespace Laplace.Core.Models;

public sealed class ExtractResult
{
    public int SucceededFiles { get; set; }
    public int FailedFiles { get; set; }
    public List<ExtractFileError> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;
}

public sealed record ExtractFileError(string RelativePath, string Reason);
