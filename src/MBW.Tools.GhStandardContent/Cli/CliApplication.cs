using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using MBW.Tools.GhStandardContent.Configuration;
using MBW.Tools.GhStandardContent.Core;
using MBW.Tools.GhStandardContent.Reporting;

namespace MBW.Tools.GhStandardContent.Cli;

internal static class CliApplication
{
    public static async Task<int> InvokeAsync(string[] args)
    {
        RootCommand root = BuildRootCommand();
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await root.Parse(args).InvokeAsync(new InvocationConfiguration(), cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    internal static RootCommand BuildRootCommand()
    {
        RootCommand root = new("Standardize content across GitHub repositories and local worktrees.");

        Option<OutputFormat> format = new("--format")
        {
            Description = "Output format: text or json.",
            DefaultValueFactory = _ => OutputFormat.Text,
            Recursive = true
        };
        Option<ColorMode> color = new("--color")
        {
            Description = "Color mode: auto, always, or never.",
            DefaultValueFactory = _ => ColorMode.Auto,
            Recursive = true
        };
        Option<OutputVerbosity> verbosity = new("--verbosity")
        {
            Description = "Output detail: quiet, normal, or detailed.",
            DefaultValueFactory = _ => OutputVerbosity.Normal,
            Recursive = true
        };
        root.Options.Add(format);
        root.Options.Add(color);
        root.Options.Add(verbosity);

        AddValidateCommand(root, format, color, verbosity);
        AddRunCommand(root, RunMode.Check, format, color, verbosity);
        AddRunCommand(root, RunMode.Apply, format, color, verbosity);
        return root;
    }

    private static void AddValidateCommand(
        RootCommand root, Option<OutputFormat> format, Option<ColorMode> color, Option<OutputVerbosity> verbosity)
    {
        Argument<string> config = ConfigurationArgument();
        Command command = new("validate", "Validate configuration and source files without repository access.");
        command.Arguments.Add(config);
        command.SetAction(async (parseResult, token) =>
        {
            RunOptions options = Defaults(
                RunMode.Validate,
                parseResult.GetRequiredValue(config),
                parseResult.GetValue(format),
                parseResult.GetValue(color),
                parseResult.GetValue(verbosity));
            return await RunAsync(options, token);
        });
        root.Subcommands.Add(command);
    }

    private static void AddRunCommand(
        RootCommand root,
        RunMode mode,
        Option<OutputFormat> format,
        Option<ColorMode> color,
        Option<OutputVerbosity> verbosity)
    {
        Argument<string> config = ConfigurationArgument();
        Option<string[]> repositories = new("--repository", "-r")
        {
            Description = "Repository owner/name or an unambiguous short name. Repeat to select multiple repositories.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
        };
        Option<string?> local = new("--local") { Description = "Operate on a local Git worktree instead of GitHub." };
        Option<Uri> githubApi = new("--github-api")
        {
            Description = "GitHub or GitHub Enterprise API base URI.",
            DefaultValueFactory = _ => new Uri("https://api.github.com/")
        };
        Option<Uri?> proxy = new("--proxy") { Description = "HTTP proxy URI." };
        Option<int> parallelism = new("--parallelism")
        {
            Description = "Maximum repositories processed concurrently (1-16).",
            DefaultValueFactory = _ => 4
        };
        parallelism.Validators.Add(result =>
        {
            int value = result.GetValueOrDefault<int>();
            if (value is < 1 or > 16)
                result.AddError("--parallelism must be between 1 and 16.");
        });
        Option<string> branch = new("--branch")
        {
            Description = "Dedicated update branch name.",
            DefaultValueFactory = _ => "feature/auto-contents"
        };
        Option<string> author = new("--commit-author")
        {
            Description = "Commit author name.",
            DefaultValueFactory = _ => "Auto Contents"
        };
        Option<string> email = new("--commit-email")
        {
            Description = "Commit author email.",
            DefaultValueFactory = _ => "AutoContents@example.org"
        };
        Option<string[]> labels = new("--label")
        {
            Description = "Pull-request label. Repeat for multiple labels.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
        };
        Option<string?> metaReference = new("--meta-reference")
        {
            Description = "Reference stored in metadata. Pass an empty value to remove the existing reference."
        };
        Option<OrphanPolicy> orphanPolicy = new("--orphaned-files")
        {
            Description = "Handling for files that are no longer managed: error, keep, or delete.",
            DefaultValueFactory = _ => OrphanPolicy.Error
        };

        Command command = new(mode == RunMode.Check ? "check" : "apply",
            mode == RunMode.Check
                ? "Read repositories and report pending changes without writing."
                : "Apply standard content locally or through GitHub pull requests.");
        command.Arguments.Add(config);
        foreach (Option option in new Option[]
                 {
                     repositories, local, githubApi, proxy, parallelism, branch, author, email, labels,
                     metaReference, orphanPolicy
                 })
            command.Options.Add(option);

        command.SetAction(async (parseResult, token) =>
        {
            RunOptions options = new(
                mode,
                parseResult.GetRequiredValue(config),
                parseResult.GetValue(format),
                parseResult.GetValue(color),
                parseResult.GetValue(verbosity),
                parseResult.GetValue(repositories) ?? [],
                parseResult.GetValue(local),
                parseResult.GetRequiredValue(githubApi),
                parseResult.GetValue(proxy),
                parseResult.GetValue(parallelism),
                parseResult.GetRequiredValue(branch),
                parseResult.GetRequiredValue(author),
                parseResult.GetRequiredValue(email),
                parseResult.GetValue(labels) ?? [],
                parseResult.GetValue(metaReference),
                parseResult.GetValue(orphanPolicy));
            return await RunAsync(options, token);
        });
        root.Subcommands.Add(command);
    }

    private static Argument<string> ConfigurationArgument() => new("CONFIG")
    {
        Description = "Path to the repository configuration JSON/JSONC file."
    };

    private static RunOptions Defaults(
        RunMode mode, string config, OutputFormat format, ColorMode color, OutputVerbosity verbosity) =>
        new(mode, config, format, color, verbosity, [], null, new Uri("https://api.github.com/"), null, 4,
            "feature/auto-contents", "Auto Contents", "AutoContents@example.org", [], null, OrphanPolicy.Error);

    private static async Task<int> RunAsync(RunOptions options, CancellationToken cancellationToken)
    {
        IRunReporter reporter = RunReporter.Create(options);
        try
        {
            RunOrchestrator orchestrator = new(new ConfigurationLoader(), new ContentPlanner());
            RunSummary summary = await reporter.RunWithProgressAsync(
                progress => orchestrator.RunAsync(options, progress, cancellationToken));
            reporter.Write(summary);
            return summary.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            reporter.WriteCancellation();
            return 130;
        }
    }
}
