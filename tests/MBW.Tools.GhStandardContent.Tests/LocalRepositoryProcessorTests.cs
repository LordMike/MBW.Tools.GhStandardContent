using MBW.Tools.GhStandardContent.Core;
using MBW.Tools.GhStandardContent.Repositories;

namespace MBW.Tools.GhStandardContent.Tests;

public sealed class LocalRepositoryProcessorTests
{
    [Fact]
    public async Task CheckIsReadOnlyAndApplyWritesOnlyThePlan()
    {
        using TestDirectory directory = new();
        Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, ".git"));
        ContentPlanner planner = new(() => new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        LocalRepositoryProcessor processor = new(directory.Path, planner);
        DesiredRepository desired = new("owner/repo", ["standard"],
            new Dictionary<string, byte[]> { ["nested/file.txt"] = "desired\n"u8.ToArray() }, null);

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RepositoryResult check = await processor.ProcessAsync(desired, Options(RunMode.Check, directory.Path), cancellationToken);
        Assert.Equal(RepositoryStatus.ChangesPending, check.Status);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "nested", "file.txt")));

        RepositoryResult apply = await processor.ProcessAsync(desired, Options(RunMode.Apply, directory.Path), cancellationToken);
        Assert.Equal(RepositoryStatus.Applied, apply.Status);
        Assert.Equal("desired\n", await File.ReadAllTextAsync(
            System.IO.Path.Combine(directory.Path, "nested", "file.txt"), cancellationToken));
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, ContentPlanner.MetadataPath)));

        RepositoryResult second = await processor.ProcessAsync(desired, Options(RunMode.Check, directory.Path), cancellationToken);
        Assert.Equal(RepositoryStatus.UpToDate, second.Status);
        Assert.Empty(second.Operations);
    }

    [Fact]
    public void ConstructorRequiresGitWorktree()
    {
        using TestDirectory directory = new();
        Assert.Throws<InvalidOperationException>(() => new LocalRepositoryProcessor(directory.Path, new ContentPlanner()));
    }

    private static RunOptions Options(RunMode mode, string localPath) => new(
        mode, "repos.json", OutputFormat.Text, ColorMode.Never, OutputVerbosity.Quiet, [], localPath,
        new Uri("https://api.github.com/"), null, 1, "feature/auto-contents", "Author", "author@example.org",
        [], null, OrphanPolicy.Error);
}
