using EPDeskExtractionWorker.Configuration;
using EPDeskExtractionWorker.Services.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionWorker.Tests;

public sealed class TemporaryWorkspaceTests
{
    [Fact]
    public async Task Workspace_ContainsPathsAndDeletesEverythingOnDispose()
    {
        using var files = new TestFiles();
        var factory = CreateFactory(files.Root);
        var workspace = factory.Create(Guid.NewGuid(), "lease_ABC-123!");
        var workspacePath = workspace.WorkspacePath;

        Assert.StartsWith(
            Path.GetFullPath(files.Root) + Path.DirectorySeparatorChar,
            workspacePath,
            StringComparison.OrdinalIgnoreCase);

        var source = workspace.SourceFilePath("../../customer.PDF");
        var derivative = workspace.DerivativeFilePath();
        Assert.Equal("source.pdf", Path.GetFileName(source));
        Assert.Equal("extraction.json", Path.GetFileName(derivative));
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(derivative, "{}");

        await workspace.DisposeAsync();

        Assert.False(Directory.Exists(workspacePath));
        Assert.True(Directory.Exists(files.Root));
    }

    [Fact]
    public void Create_RejectsLeaseTokenWithoutSafeCharacters()
    {
        using var files = new TestFiles();
        var factory = CreateFactory(files.Root);

        Assert.Throws<InvalidOperationException>(() => factory.Create(Guid.NewGuid(), "---!!!"));
    }

    [Fact]
    public void Constructor_RejectsFilesystemRoot()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.False(string.IsNullOrWhiteSpace(root));

        Assert.Throws<InvalidOperationException>(() => CreateFactory(root!));
    }

    private static TemporaryWorkspaceFactory CreateFactory(string root) =>
        new(
            Options.Create(new ExtractionWorkerOptions { TempRoot = root }),
            NullLogger<TemporaryWorkspaceFactory>.Instance);
}
