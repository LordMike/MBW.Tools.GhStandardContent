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
    Merged,
    NoChanges,
    PullRequestMissing,
    Outdated,
    CiNotReady,
    CiNotPassing,
    PullRequestNotMergeable,
    Skipped,
    Blocked,
    Failed
}
