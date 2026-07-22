namespace MBW.Tools.GhStandardContent.Core;

internal sealed record RunSummary(
    RunMode Command,
    string Result,
    IReadOnlyList<RepositoryResult> Repositories,
    IReadOnlyList<ValidationDiagnostic> Diagnostics)
{
    public int ExitCode => Command switch
    {
        RunMode.Validate when Diagnostics.Count > 0 => 1,
        RunMode.Check when Repositories.Any(result => result.Status is RepositoryStatus.Failed or RepositoryStatus.Blocked) => 3,
        RunMode.Check when Repositories.Any(result => result.Status is RepositoryStatus.ChangesPending or RepositoryStatus.PullRequestOpen) => 2,
        RunMode.Apply when Repositories.Any(result => result.Status is RepositoryStatus.Failed or RepositoryStatus.Blocked) => 3,
        _ when Diagnostics.Count > 0 => 1,
        _ => 0
    };
}