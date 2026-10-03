using MBW.Tools.GhStandardContent.Core;
using Spectre.Console;

namespace MBW.Tools.GhStandardContent.Reporting;

internal sealed class TextRunReporter : IRunReporter
{
    private readonly IAnsiConsole _console;
    private readonly bool _interactive;
    private readonly OutputVerbosity _verbosity;
    private readonly object _sync = new();

    public TextRunReporter(ColorMode color, OutputVerbosity verbosity, bool? interactive = null)
    {
        _verbosity = verbosity;
        _interactive = interactive ?? !Console.IsOutputRedirected;
        AnsiConsoleSettings settings = new()
        {
            Ansi = color switch
            {
                ColorMode.Always => AnsiSupport.Yes,
                ColorMode.Never => AnsiSupport.No,
                _ => AnsiSupport.Detect
            },
            ColorSystem = color == ColorMode.Never ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Out = new AnsiConsoleOutput(Console.Out)
        };
        _console = AnsiConsole.Create(settings);
    }

    public Task<RunSummary> RunWithProgressAsync(Func<Action<RunProgress>?, Task<RunSummary>> operation)
    {
        if (_verbosity == OutputVerbosity.Quiet || !_interactive)
            return operation(null);

        return _console.Progress()
            .AutoClear(true)
            .HideCompleted(false)
            .Columns(
                new ProgressBarColumn
                {
                    CompletedStyle = new Style(Color.Blue),
                    RemainingStyle = new Style(Color.Grey)
                },
                new TaskDescriptionColumn())
            .StartAsync(async context =>
            {
                ProgressTask? task = null;
                return await operation(progress =>
                {
                    lock (_sync)
                    {
                        double maximum = Math.Max(1, progress.Total);
                        string description = Markup.Escape(FormatProgress(progress));
                        task ??= context.AddTask(description, maxValue: maximum);
                        task.MaxValue = maximum;
                        task.Value = progress.Total == 0 ? maximum : progress.Completed;
                        task.Description = description;
                    }
                });
            });
    }

    internal static string FormatProgress(RunProgress progress)
    {
        string status = progress.Phase switch
        {
            RunProgressPhase.Starting => "Starting repositories",
            RunProgressPhase.Processing when progress.StatusRepository is not null =>
                $"Processing {progress.StatusRepository}",
            RunProgressPhase.Processing => "Processing repositories",
            RunProgressPhase.Waiting when progress.StatusRepository is not null =>
                $"Waiting for {progress.StatusRepository}",
            RunProgressPhase.Waiting => "Waiting for repositories",
            RunProgressPhase.Finalizing => "Finalizing results",
            _ => throw new ArgumentOutOfRangeException(nameof(progress))
        };
        return $"{progress.Completed}/{progress.Total} complete · {progress.Running} running · {status}";
    }

    public void Write(RunSummary summary)
    {
        lock (_sync)
        {
            foreach (ValidationDiagnostic diagnostic in summary.Diagnostics)
            {
                string location = diagnostic.Location is null ? string.Empty : $" [grey]({Markup.Escape(diagnostic.Location)})[/]";
                _console.MarkupLine($"[red]✗ {Markup.Escape(diagnostic.Code)}:[/] {Markup.Escape(diagnostic.Message)}{location}");
            }

            if (summary.Command == RunMode.Validate && summary.Diagnostics.Count == 0)
            {
                _console.MarkupLine("[green]✓ Configuration is valid.[/]");
                return;
            }

            if (summary.Repositories.Count == 0)
                return;

            Table table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Repository");
            table.AddColumn("Status");
            table.AddColumn(new TableColumn("+").RightAligned());
            table.AddColumn(new TableColumn("~").RightAligned());
            table.AddColumn(new TableColumn("−").RightAligned());
            table.AddColumn("Details");

            foreach (RepositoryResult result in summary.Repositories)
            {
                bool showCounts = result.Status is not (
                    RepositoryStatus.UpToDate or RepositoryStatus.NoChanges or RepositoryStatus.Skipped or
                    RepositoryStatus.Blocked or RepositoryStatus.Failed);
                string adds = showCounts
                    ? result.Operations.Count(operation => operation.Kind == FileOperationKind.Add).ToString()
                    : "–";
                string updates = showCounts
                    ? result.Operations.Count(operation => operation.Kind == FileOperationKind.Update).ToString()
                    : "–";
                string deletes = showCounts
                    ? result.Operations.Count(operation => operation.Kind == FileOperationKind.Delete).ToString()
                    : "–";
                string[] details = new string?[] { result.Error?.Message, result.Detail, result.PullRequest?.Url }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray();
                table.AddRow(
                    Markup.Escape(result.Repository),
                    StatusMarkup(result),
                    adds,
                    updates,
                    deletes,
                    Markup.Escape(string.Join(" · ", details)));
            }

            _console.Write(table);
            _console.MarkupLine(Conclusion(summary));

            if (_verbosity == OutputVerbosity.Detailed)
            {
                foreach (RepositoryResult result in summary.Repositories)
                {
                    if (result.Operations.Count == 0 && result.Error?.Detail is null)
                        continue;
                    _console.MarkupLine($"\n[bold]{Markup.Escape(result.Repository)}[/]");
                    foreach (FileOperation operation in result.Operations)
                        _console.MarkupLine($"  {OperationSymbol(operation.Kind)} {Markup.Escape(operation.Path)}");
                    if (result.Error?.Detail is not null)
                        _console.WriteLine(result.Error.Detail);
                }
            }
        }
    }

    public void WriteCancellation()
    {
        lock (_sync)
            _console.MarkupLine("[yellow]Operation cancelled.[/]");
    }

    internal static string Conclusion(RunSummary summary)
    {
        IReadOnlyList<RepositoryResult> results = summary.Repositories;
        int Count(params RepositoryStatus[] statuses) => results.Count(result => statuses.Contains(result.Status));

        int problems = Count(RepositoryStatus.Failed, RepositoryStatus.Blocked);
        if (problems > 0)
            return $"[red]✗ {Plural(problems, "repository")} could not be processed. See Details above.[/]";

        return summary.Command switch
        {
            RunMode.Check => CheckConclusion(),
            RunMode.Apply => ApplyConclusion(),
            RunMode.Merge => MergeConclusion(),
            _ => NoChanges()
        };

        string CheckConclusion()
        {
            int pending = Count(RepositoryStatus.ChangesPending);
            if (pending > 0)
            {
                string action = results.Any(result => result.Target == "local")
                    ? "write them to the worktree"
                    : "open pull requests";
                return $"[yellow]△ Changes needed in {Plural(pending, "repository")}. Run 'apply' to {action}.[/]";
            }

            int behind = Count(RepositoryStatus.PullRequestBehind);
            if (behind > 0)
                return $"[yellow]↗ {Be(behind, "pull request")} behind the default branch. Run 'apply' to refresh.[/]";

            int open = Count(RepositoryStatus.PullRequestOpen);
            if (open > 0)
                return $"[blue]↗ {Be(open, "pull request")} open. Run 'merge' once CI passes.[/]";

            return NoChanges();
        }

        string ApplyConclusion()
        {
            int open = Count(RepositoryStatus.PullRequestCreated, RepositoryStatus.PullRequestUpdated,
                RepositoryStatus.PullRequestRefreshed, RepositoryStatus.PullRequestOpen);
            if (open > 0)
                return $"[cyan]↗ {Be(open, "pull request")} open. Run 'merge' once CI passes.[/]";

            int updated = Count(RepositoryStatus.FilesUpdated);
            if (updated > 0)
                return $"[cyan]✓ Files updated in {Plural(updated, "repository")}. Review and commit the changes.[/]";

            return NoChanges();
        }

        string MergeConclusion()
        {
            int attention = Count(RepositoryStatus.CiNotPassing, RepositoryStatus.PullRequestNotMergeable);
            if (attention > 0)
                return $"[red]! Manual attention needed for {Plural(attention, "pull request")}. See Details above.[/]";

            int repairable = Count(RepositoryStatus.PullRequestMissing, RepositoryStatus.Outdated);
            if (repairable > 0)
                return $"[yellow]↻ {Be(repairable, "pull request")} missing or outdated. Run 'merge --allow-updating' to repair.[/]";

            int waiting = Count(RepositoryStatus.CiNotReady, RepositoryStatus.PullRequestCreated,
                RepositoryStatus.PullRequestUpdated, RepositoryStatus.PullRequestRefreshed);
            if (waiting > 0)
                return $"[yellow]◷ {Be(waiting, "pull request")} waiting for CI. Rerun 'merge' later.[/]";

            int merged = Count(RepositoryStatus.Merged);
            return merged > 0
                ? $"[green]✓ {Plural(merged, "pull request")} merged. No further changes needed.[/]"
                : NoChanges();
        }

        static string NoChanges() => "[green]✓ No changes needed.[/]";
        static string Be(int count, string noun) => $"{Plural(count, noun)} {(count == 1 ? "is" : "are")}";
        static string Plural(int count, string noun) => count == 1
            ? $"1 {noun}"
            : noun.EndsWith('y') ? $"{count} {noun[..^1]}ies" : $"{count} {noun}s";
    }

    private static string StatusMarkup(RepositoryResult result) => result.Status switch
    {
        RepositoryStatus.UpToDate => "[green]✓ up to date[/]",
        RepositoryStatus.FilesUpdated => "[cyan]✓ files updated[/]",
        RepositoryStatus.PullRequestCreated => "[cyan]↗ PR created[/]",
        RepositoryStatus.PullRequestUpdated => "[cyan]↻ PR updated[/]",
        RepositoryStatus.ChangesPending => "[yellow]△ changes pending[/]",
        RepositoryStatus.PullRequestOpen => "[blue]↗ PR already current[/]",
        RepositoryStatus.PullRequestBehind =>
            $"[yellow]↗ PR behind by {result.PullRequest?.BehindBy ?? 0}[/]",
        RepositoryStatus.PullRequestRefreshed => "[cyan]↻ PR refreshed[/]",
        RepositoryStatus.Merged => "[cyan]✓ merged[/]",
        RepositoryStatus.NoChanges => "[green]✓ no changes[/]",
        RepositoryStatus.PullRequestMissing => "[yellow]△ PR not created[/]",
        RepositoryStatus.Outdated => "[yellow]↻ outdated[/]",
        RepositoryStatus.CiNotReady => "[yellow]◷ CI not ready[/]",
        RepositoryStatus.CiNotPassing => "[red]✗ CI not passing[/]",
        RepositoryStatus.PullRequestNotMergeable => "[yellow]! PR not mergeable[/]",
        RepositoryStatus.Skipped => "[grey]– skipped[/]",
        RepositoryStatus.Blocked => "[yellow]! blocked[/]",
        RepositoryStatus.Failed => "[red]✗ failed[/]",
        _ => Markup.Escape(result.Status.ToString())
    };

    private static string OperationSymbol(FileOperationKind kind) => kind switch
    {
        FileOperationKind.Add => "[green]+[/]",
        FileOperationKind.Update => "[yellow]~[/]",
        FileOperationKind.Delete => "[red]−[/]",
        _ => "?"
    };
}
