namespace MBW.Tools.GhStandardContent.Core;

internal sealed record RepositoryAssessment(
    string Repository,
    string Target,
    RepositoryAssessmentStatus Status,
    IReadOnlyList<FileOperation> Operations,
    PullRequestInfo? PullRequest,
    IReadOnlyList<PullRequestIssue> Issues,
    string? BaseSha = null,
    string? HeadSha = null,
    RepositoryError? Error = null)
{
    public RepositoryReason? PrimaryReason => Issues.Count == 0
        ? null
        : Issues[0] switch
        {
            PullRequestIssue.ContentMismatch => RepositoryReason.ContentMismatch,
            PullRequestIssue.UnexpectedChanges => RepositoryReason.UnexpectedChanges,
            PullRequestIssue.BehindBase => RepositoryReason.BehindBase,
            PullRequestIssue.LabelsMissing => RepositoryReason.LabelsMissing,
            _ => null
        };

    public string? IssueDetail => Issues.Count == 0
        ? null
        : string.Join(", ", Issues.Select(issue => issue switch
        {
            PullRequestIssue.ContentMismatch => "generated content differs",
            PullRequestIssue.UnexpectedChanges => "PR contains unexpected changes",
            PullRequestIssue.BehindBase => $"PR is behind the default branch by {PullRequest?.BehindBy ?? 0}",
            PullRequestIssue.LabelsMissing => "configured labels are missing",
            _ => issue.ToString()
        }));
}
