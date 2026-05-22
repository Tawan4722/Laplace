namespace Laplace.Core.Models;

public sealed class ExtractArchiveOptions
{
    public bool Overwrite { get; set; }
    public bool VerifyChecksums { get; set; } = true;
    public IReadOnlySet<long>? SelectedEntryIds { get; set; }
}
