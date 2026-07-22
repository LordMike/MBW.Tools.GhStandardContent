using MBW.Tools.GhStandardContent.Core;
using Spectre.Console;

namespace MBW.Tools.GhStandardContent.Reporting;

internal sealed class TextRunReporter : IRunReporter
{
    private readonly IAnsiConsole _console;
    private readonly OutputVerbosity _verbosity;
    private readonly object _sync = new();

    public TextRunReporter(ColorMode color, OutputVerbosity verbosity)
    {
        _verbosity = verbosity;
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

    public void RepositoryStarted(string repository)
    {
        if (_verbosity == OutputVerbosity.Quiet)
            return;

        lock (_sync)
            _console.MarkupLine($"[grey]→[/] {Markup.Escape(repository)}");
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
        RepositoryStatus.PullRequestOpen => "[yellow]↗ pull request open[/]",
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