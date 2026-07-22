using System.Text.Json;
using MBW.Tools.GhStandardContent.Cli;
using MBW.Tools.GhStandardContent.Core;
using MBW.Tools.GhStandardContent.Reporting;

namespace MBW.Tools.GhStandardContent.Tests;

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;

[Collection("Console")]
public sealed class CliAndReporterTests
{
    [Fact]
    public void CliRequiresOneOfTheNewCommands()
    {
        Assert.NotEmpty(CliApplication.BuildRootCommand().Parse(["repos.json"]).Errors);
        Assert.Empty(CliApplication.BuildRootCommand().Parse(["validate", "repos.json"]).Errors);
        Assert.Empty(CliApplication.BuildRootCommand().Parse([
            "check", "repos.json", "--local", ".", "--orphaned-files", "keep", "--format", "json"
        ]).Errors);
    }

    [Fact]
    public void JsonReporterEmitsTypedResultsWithoutFileContents()
    {
        TextWriter original = Console.Out;
        using StringWriter writer = new();
        Console.SetOut(writer);
        try
        {
            RunSummary summary = new(RunMode.Check, "changesPending",
            [
                new RepositoryResult("owner/repo", "local", RepositoryStatus.ChangesPending,
                    [new FileOperation("file.txt", FileOperationKind.Update, "secret-content"u8.ToArray())])
            ], []);

            new JsonRunReporter().Write(summary);

            using JsonDocument json = JsonDocument.Parse(writer.ToString());
            Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("file.txt", json.RootElement.GetProperty("repositories")[0]
                .GetProperty("changes")[0].GetProperty("path").GetString());
            Assert.DoesNotContain("secret-content", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
