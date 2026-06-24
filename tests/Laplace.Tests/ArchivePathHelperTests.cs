using Laplace.Core.Models;
using Laplace.Core.Services;
using Xunit;

namespace Laplace.Tests;

public sealed class ArchivePathHelperTests
{
    [Fact]
    public async Task ResolveBesideArchivePath_FileInput_UsesFileStem()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "report.pdf");
            await File.WriteAllTextAsync(sourceFile, "payload");

            var path = ArchivePathHelper.ResolveBesideArchivePath(sourceFile);

            Assert.Equal(Path.Combine(root, "report.lpc"), path);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void ResolveBesideArchivePath_FolderInput_UsesFolderName()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFolder = Path.Combine(root, "Photos");
            Directory.CreateDirectory(sourceFolder);

            var path = ArchivePathHelper.ResolveBesideArchivePath(sourceFolder);

            Assert.Equal(Path.Combine(root, "Photos.lpc"), path);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ResolveBesideArchivePath_ExistingArchive_UsesNumberedFallback()
    {
        var root = CreateTempFolder();
        try
        {
            var sourceFile = Path.Combine(root, "report.pdf");
            await File.WriteAllTextAsync(sourceFile, "payload");
            await File.WriteAllTextAsync(Path.Combine(root, "report.lpc"), "existing archive");

            var path = ArchivePathHelper.ResolveBesideArchivePath(sourceFile);

            Assert.Equal(Path.Combine(root, "report (2).lpc"), path);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }


    private static string CreateTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"laplace-path-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
