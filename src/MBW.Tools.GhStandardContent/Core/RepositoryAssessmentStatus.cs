namespace MBW.Tools.GhStandardContent.Core;

internal enum RepositoryAssessmentStatus
{
    NoChanges,
    ChangesPending,
    PullRequestMissing,
    PullRequestCurrent,
    PullRequestOutdated,
    Blocked
}
