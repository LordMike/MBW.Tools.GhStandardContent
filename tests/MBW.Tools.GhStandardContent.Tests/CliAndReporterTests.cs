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

    [Fact]
    public void TextReporterDescribesCurrentPullRequestWithoutSuccessColorSemantics()
    {
        TextWriter original = Console.Out;
        using StringWriter writer = new();
        Console.SetOut(writer);
        try
        {
            RunSummary summary = new(RunMode.Apply, "changesPending",
            [
                new RepositoryResult("owner/repo", "github", RepositoryStatus.PullRequestOpen, [],
                    new PullRequestInfo(42, "https://example.test/pull/42", false))
            ], []);

            new TextRunReporter(ColorMode.Never, OutputVerbosity.Normal).Write(summary);

            Assert.Contains("PR already current", writer.ToString(), StringComparison.Ordinal);
            Assert.Contains("https://example.test/pull/42", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void ProgressDescriptionReflectsTypedPhase()
    {
        (RunProgressPhase Phase, string? Repository, string Expected)[] cases =
        [
            (RunProgressPhase.Starting, null, "Starting repositories"),
            (RunProgressPhase.Processing, "owner/repo", "Processing owner/repo"),
            (RunProgressPhase.Waiting, "owner/repo", "Waiting for owner/repo"),
            (RunProgressPhase.Finalizing, null, "Finalizing results")
        ];

        foreach ((RunProgressPhase phase, string? repository, string expectedStatus) in cases)
        {
            RunProgress progress = new(48, 13, 4, 31, ["owner/repo"], phase, repository);

            string description = TextRunReporter.FormatProgress(progress);

            Assert.Equal($"13/48 complete · 4 running · {expectedStatus}", description);
        }
    }

    [Fact]
    public async Task NonInteractiveTextReporterSuppressesTransientProgress()
    {
        TextRunReporter reporter = new(ColorMode.Never, OutputVerbosity.Normal, interactive: false);
        RunSummary expected = new(RunMode.Check, "success", [], []);
        bool invoked = false;

        RunSummary actual = await reporter.RunWithProgressAsync(progress =>
        {
            invoked = true;
            Assert.Null(progress);
            return Task.FromResult(expected);
        });

        Assert.True(invoked);
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task InteractiveTextReporterCreatesProgressTaskWithFirstDescription()
    {
        TextWriter original = Console.Out;
        using StringWriter writer = new();
        Console.SetOut(writer);
        try
        {
            TextRunReporter reporter = new(ColorMode.Never, OutputVerbosity.Normal, interactive: true);
            RunSummary expected = new(RunMode.Check, "success", [], []);

            RunSummary actual = await reporter.RunWithProgressAsync(progress =>
            {
                Assert.NotNull(progress);
                progress(new RunProgress(2, 0, 1, 1, ["owner/repo"],
                    RunProgressPhase.Processing, "owner/repo"));
                progress(new RunProgress(2, 2, 0, 0, [], RunProgressPhase.Finalizing, null));
                return Task.FromResult(expected);
            });

            Assert.Same(expected, actual);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public async Task JsonReporterSuppressesTransientProgress()
    {
        JsonRunReporter reporter = new();
        RunSummary expected = new(RunMode.Check, "success", [], []);

        RunSummary actual = await reporter.RunWithProgressAsync(progress =>
        {
            Assert.Null(progress);
            return Task.FromResult(expected);
        });

        Assert.Same(expected, actual);
    }
}
