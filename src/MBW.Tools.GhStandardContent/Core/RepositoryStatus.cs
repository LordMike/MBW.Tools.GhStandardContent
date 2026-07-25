namespace MBW.Tools.GhStandardContent.Core;

internal enum RepositoryStatus
{
    UpToDate,
    ChangesPending,
    Applied,
    PullRequestOpen,
    PullRequestBehind,
    PullRequestRefreshed,
    Skipped,
    Blocked,
    Failed
}
