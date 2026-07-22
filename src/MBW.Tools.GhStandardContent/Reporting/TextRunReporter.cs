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
                int adds = result.Operations.Count(operation => operation.Kind == FileOperationKind.Add);
                int updates = result.Operations.Count(operation => operation.Kind == FileOperationKind.Update);
                int deletes = result.Operations.Count(operation => operation.Kind == FileOperationKind.Delete);
                string details = result.Error?.Message ?? result.PullRequest?.Url ?? string.Empty;
                table.AddRow(
                    Markup.Escape(result.Repository),
                    StatusMarkup(result.Status),
                    adds.ToString(),
                    updates.ToString(),
                    deletes.ToString(),
                    Markup.Escape(details));
            }

            _console.Write(table);

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

    private static string StatusMarkup(RepositoryStatus status) => status switch
    {
        RepositoryStatus.UpToDate => "[green]✓ up to date[/]",
        RepositoryStatus.Applied => "[cyan]✓ applied[/]",
        RepositoryStatus.ChangesPending => "[yellow]△ changes pending[/]",
        RepositoryStatus.PullRequestOpen => "[blue]↗ PR already current[/]",
        RepositoryStatus.Skipped => "[grey]– skipped[/]",
        RepositoryStatus.Blocked => "[yellow]! blocked[/]",
        RepositoryStatus.Failed => "[red]✗ failed[/]",
        _ => Markup.Escape(status.ToString())
    };

    private static string OperationSymbol(FileOperationKind kind) => kind switch
    {
        FileOperationKind.Add => "[green]+[/]",
        FileOperationKind.Update => "[yellow]~[/]",
        FileOperationKind.Delete => "[red]−[/]",
        _ => "?"
    };
}
