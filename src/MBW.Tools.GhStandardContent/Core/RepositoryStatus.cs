namespace MBW.Tools.GhStandardContent.Core;

internal enum RepositoryStatus
{
    UpToDate,
    ChangesPending,
    Applied,
    PullRequestOpen,
    Skipped,
    Blocked,
    Failed
}