namespace MBW.Tools.GhStandardContent.Core;

internal sealed record RepositoryResult(
    string Repository,
    string Target,
    RepositoryStatus Status,
    IReadOnlyList<FileOperation> Operations,
    PullRequestInfo? PullRequest = null,
    RepositoryError? Error = null,
    RepositoryReason? Reason = null,
    string? Detail = null)
{
    public static RepositoryResult Failed(string repository, string target, string code, string message, string? detail = null) =>
        new(repository, target, RepositoryStatus.Failed, [], null, new RepositoryError(code, message, detail));
}
