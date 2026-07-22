using System.Text.Json;
using System.CommandLine;
using System.CommandLine.Parsing;
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
        using EnvironmentVariableScope environment = new();
        Assert.NotEmpty(CliApplication.BuildRootCommand().Parse(["repos.json"]).Errors);
        Assert.NotEmpty(CliApplication.BuildRootCommand().Parse(["validate"]).Errors);
        Assert.Empty(CliApplication.BuildRootCommand().Parse(["validate", "repos.json"]).Errors);
        Assert.Empty(CliApplication.BuildRootCommand().Parse([
            "check", "repos.json", "--local", ".", "--orphaned-files", "keep", "--format", "json"
        ]).Errors);
    }

    [Fact]
    public void ConfigDefaultsFromEnvironmentAndExplicitArgumentOverridesIt()
    {
        using EnvironmentVariableScope environment = new();
        environment.Set(EnvironmentDefaults.Config, "environment.json");
        RootCommand root = CliApplication.BuildRootCommand();
        Command validate = root.Subcommands.Single(item => item.Name == "validate");
        Argument<string> config = validate.Arguments.OfType<Argument<string>>().Single();

        ParseResult environmentParse = root.Parse(["validate"]);
        ParseResult explicitParse = root.Parse(["validate", "explicit.json"]);

        Assert.Empty(environmentParse.Errors);
        Assert.Equal("environment.json", environmentParse.GetValue(config));
        Assert.Empty(explicitParse.Errors);
        Assert.Equal("explicit.json", explicitParse.GetValue(config));
    }

    [Fact]
    public void CliReadsOperationalDefaultsFromEnvironment()
    {
        using EnvironmentVariableScope environment = new();
        environment.Set(EnvironmentDefaults.Repositories, " owner/one ; ;owner/two ");
        environment.Set(EnvironmentDefaults.Local, "../checkout");
        environment.Set(EnvironmentDefaults.GitHubApi, "https://github.example/api/v3/");
        environment.Set(EnvironmentDefaults.Proxy, "http://proxy.example:8080/");
        environment.Set(EnvironmentDefaults.Parallelism, "7");
        environment.Set(EnvironmentDefaults.Branch, "automation/content");
        environment.Set(EnvironmentDefaults.CommitAuthor, "Automation User");
        environment.Set(EnvironmentDefaults.CommitEmail, "automation@example.test");
        environment.Set(EnvironmentDefaults.Labels, " maintenance ; content ");
        environment.Set(EnvironmentDefaults.MetaReference, "environment-reference");
        environment.Set(EnvironmentDefaults.OrphanedFiles, "delete");
        RootCommand root = CliApplication.BuildRootCommand();
        Command command = root.Subcommands.Single(item => item.Name == "check");

        ParseResult parse = root.Parse(["check", "repos.json"]);

        Assert.Empty(parse.Errors);
        Assert.Equal(["owner/one", "owner/two"], parse.GetValue(Option<string[]>(command, "--repository"))!);
        Assert.Equal("../checkout", parse.GetValue(Option<string?>(command, "--local")));
        Assert.Equal(new Uri("https://github.example/api/v3/"), parse.GetValue(Option<Uri>(command, "--github-api")));
        Assert.Equal(new Uri("http://proxy.example:8080/"), parse.GetValue(Option<Uri?>(command, "--proxy")));
        Assert.Equal(7, parse.GetValue(Option<int>(command, "--parallelism")));
        Assert.Equal("automation/content", parse.GetValue(Option<string>(command, "--branch")));
        Assert.Equal("Automation User", parse.GetValue(Option<string>(command, "--commit-author")));
        Assert.Equal("automation@example.test", parse.GetValue(Option<string>(command, "--commit-email")));
        Assert.Equal(["maintenance", "content"], parse.GetValue(Option<string[]>(command, "--label"))!);
        Assert.Equal("environment-reference", parse.GetValue(Option<string?>(command, "--meta-reference")));
        Assert.Equal(OrphanPolicy.Delete, parse.GetValue(Option<OrphanPolicy>(command, "--orphaned-files")));
    }

    [Fact]
    public void ExplicitCliOptionsReplaceEnvironmentDefaults()
    {
        using EnvironmentVariableScope environment = new();
        environment.Set(EnvironmentDefaults.Repositories, "env/one;env/two");
        environment.Set(EnvironmentDefaults.Labels, "environment-label");
        environment.Set(EnvironmentDefaults.Parallelism, "2");
        environment.Set(EnvironmentDefaults.OrphanedFiles, "delete");
        RootCommand root = CliApplication.BuildRootCommand();
        Command command = root.Subcommands.Single(item => item.Name == "check");

        ParseResult parse = root.Parse([
            "check", "repos.json", "-r", "cli/one", "cli/two", "--label", "cli-label",
            "--parallelism", "8", "--orphaned-files", "keep"
        ]);

        Assert.Empty(parse.Errors);
        Assert.Equal(["cli/one", "cli/two"], parse.GetValue(Option<string[]>(command, "--repository"))!);
        Assert.Equal(["cli-label"], parse.GetValue(Option<string[]>(command, "--label"))!);
        Assert.Equal(8, parse.GetValue(Option<int>(command, "--parallelism")));
        Assert.Equal(OrphanPolicy.Keep, parse.GetValue(Option<OrphanPolicy>(command, "--orphaned-files")));
    }

    [Fact]
    public void InvalidEnvironmentDefaultsProduceParseErrors()
    {
        using EnvironmentVariableScope environment = new();
        environment.Set(EnvironmentDefaults.GitHubApi, "not a uri");
        environment.Set(EnvironmentDefaults.Proxy, "also not a uri");
        environment.Set(EnvironmentDefaults.Parallelism, "99");
        environment.Set(EnvironmentDefaults.OrphanedFiles, "destroy");

        ParseResult parse = CliApplication.BuildRootCommand().Parse(["check", "repos.json"]);

        string errors = string.Join("\n", parse.Errors.Select(error => error.Message));
        Assert.Contains(EnvironmentDefaults.GitHubApi, errors, StringComparison.Ordinal);
        Assert.Contains(EnvironmentDefaults.Proxy, errors, StringComparison.Ordinal);
        Assert.Contains(EnvironmentDefaults.Parallelism, errors, StringComparison.Ordinal);
        Assert.Contains(EnvironmentDefaults.OrphanedFiles, errors, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitParallelismStillEnforcesRange()
    {
        using EnvironmentVariableScope environment = new();

        ParseResult parse = CliApplication.BuildRootCommand().Parse([
            "check", "repos.json", "--parallelism", "99"
        ]);

        Assert.Contains(parse.Errors,
            error => error.Message.Contains("--parallelism", StringComparison.Ordinal));
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

    private static Option<T> Option<T>(Command command, string name) =>
        command.Options.OfType<Option<T>>().Single(option =>
            Normalize(option.Name).Equals(Normalize(name), StringComparison.Ordinal) ||
            option.Aliases.Any(alias => Normalize(alias).Equals(Normalize(name), StringComparison.Ordinal)));

    private static string Normalize(string value) => value.TrimStart('-');

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private static readonly string[] Names =
        [
            EnvironmentDefaults.Config,
            EnvironmentDefaults.Repositories,
            EnvironmentDefaults.Local,
            EnvironmentDefaults.GitHubApi,
            EnvironmentDefaults.Proxy,
            EnvironmentDefaults.Parallelism,
            EnvironmentDefaults.Branch,
            EnvironmentDefaults.CommitAuthor,
            EnvironmentDefaults.CommitEmail,
            EnvironmentDefaults.Labels,
            EnvironmentDefaults.MetaReference,
            EnvironmentDefaults.OrphanedFiles
        ];

        private readonly Dictionary<string, string?> _original = Names.ToDictionary(
            name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);

        public EnvironmentVariableScope()
        {
            foreach (string name in Names)
                Environment.SetEnvironmentVariable(name, null);
        }

        public void Set(string name, string value) => Environment.SetEnvironmentVariable(name, value);

        public void Dispose()
        {
            foreach ((string name, string? value) in _original)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
