namespace MBW.Tools.GhStandardContent.Core;

internal enum RepositoryStatus
{
    UpToDate,
    ChangesPending,
    FilesUpdated,
    PullRequestCreated,
    PullRequestUpdated,
    PullRequestOpen,
    PullRequestBehind,
    PullRequestRefreshed,
    Skipped,
    Blocked,
    Failed
}
