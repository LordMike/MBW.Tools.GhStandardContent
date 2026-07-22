using MBW.Tools.GhStandardContent.Configuration;
using MBW.Tools.GhStandardContent.Core;

namespace MBW.Tools.GhStandardContent.Tests;

public sealed class ConfigurationLoaderTests
{
    [Fact]
    public async Task Load_AcceptsJsoncAndBuildsNormalizedDesiredContent()
    {
        using TestDirectory directory = new();
        directory.File("content/source.txt", "one\r\ntwo\r\n");
        string config = directory.File("repos.json", """
            {
              // Existing JSONC files remain supported.
              "content": {
                "standard": { "docs\\file.txt": "content/source.txt", },
              },
              "repositories": {
                "Owner/Repo": { "standard": true, },
              },
            }
            """);
        ConfigurationLoader loader = new();

        (LoadedConfiguration? loaded, IReadOnlyList<ValidationDiagnostic> diagnostics) =
            await loader.LoadAsync(config, TestContext.Current.CancellationToken);

        Assert.Empty(diagnostics);
        Assert.NotNull(loaded);
        DesiredRepository desired = await loader.BuildDesiredAsync(loaded, "Owner/Repo", null, TestContext.Current.CancellationToken);
        Assert.Equal("one\ntwo\n", System.Text.Encoding.UTF8.GetString(desired.Files["docs/file.txt"]));
    }

    [Fact]
    public async Task Load_IgnoresUnrelatedRepositoryMetadataWhileReportingContentErrors()
    {
        using TestDirectory directory = new();
        string config = directory.File("repos.json", """
            {
              "content": {
                "standard": {
                  "../escape": "missing.txt",
                  "valid.txt": "missing-too.txt"
                }
              },
              "repositories": {
                "owner/repo": {
                  "description": "Shared repository metadata",
                  "branch": "main",
                  "topics": ["dotnet", "tools"],
                  "pages": { "path": "/" },
                  "public": false
                }
              }
            }
            """);

        (_, IReadOnlyList<ValidationDiagnostic> diagnostics) =
            await new ConfigurationLoader().LoadAsync(config, TestContext.Current.CancellationToken);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "path.invalid");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "source.notFound");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code.StartsWith("repository.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Load_RequiresKnownProfileSelectorsToBeBoolean()
    {
        using TestDirectory directory = new();
        directory.File("source.txt", "content");
        string config = directory.File("repos.json", """
            {
              "content": {
                "standard": { "target.txt": "source.txt" }
              },
              "repositories": {
                "owner/repo": {
                  "standard": "yes",
                  "topics": ["ignored"]
                }
              }
            }
            """);

        (_, IReadOnlyList<ValidationDiagnostic> diagnostics) =
            await new ConfigurationLoader().LoadAsync(config, TestContext.Current.CancellationToken);

        ValidationDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("repository.invalidProfileSelector", diagnostic.Code);
    }

    [Fact]
    public async Task Load_ReportsTargetsDuplicatedAcrossSelectedProfiles()
    {
        using TestDirectory directory = new();
        directory.File("a.txt", "a");
        directory.File("b.txt", "b");
        string config = directory.File("repos.json", """
            {
              "content": {
                "one": { "same.txt": "a.txt" },
                "two": { "same.txt": "b.txt" }
              },
              "repositories": {
                "owner/repo": { "one": true, "two": true }
              }
            }
            """);

        (_, IReadOnlyList<ValidationDiagnostic> diagnostics) =
            await new ConfigurationLoader().LoadAsync(config, TestContext.Current.CancellationToken);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "repository.duplicateTarget");
    }
}
