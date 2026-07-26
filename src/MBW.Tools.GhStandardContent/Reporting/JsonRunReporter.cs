using System.Text.Json;
using System.Text.Json.Serialization;
using MBW.Tools.GhStandardContent.Core;

namespace MBW.Tools.GhStandardContent.Reporting;

internal sealed class JsonRunReporter : IRunReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public Task<RunSummary> RunWithProgressAsync(Func<Action<RunProgress>?, Task<RunSummary>> operation) =>
        operation(null);

    public void Write(RunSummary summary)
    {
        object payload = new
        {
            schemaVersion = 1,
            command = summary.Command,
            result = summary.Result,
            summary = new
            {
                total = summary.Repositories.Count,
                upToDate = summary.Repositories.Count(item => item.Status is
                    RepositoryStatus.UpToDate or RepositoryStatus.NoChanges),
                changed = summary.Repositories.Count(item => item.Status is
                    RepositoryStatus.FilesUpdated or RepositoryStatus.PullRequestCreated or
                    RepositoryStatus.PullRequestUpdated or RepositoryStatus.ChangesPending or
                    RepositoryStatus.PullRequestRefreshed or RepositoryStatus.Merged),
                pullRequests = summary.Repositories.Count(item => item.Status is
                    RepositoryStatus.PullRequestCreated or RepositoryStatus.PullRequestUpdated or
                    RepositoryStatus.PullRequestOpen or RepositoryStatus.PullRequestBehind or
                    RepositoryStatus.PullRequestRefreshed or RepositoryStatus.Merged or
                    RepositoryStatus.Outdated or RepositoryStatus.CiNotReady or
                    RepositoryStatus.CiNotPassing or RepositoryStatus.PullRequestNotMergeable),
                merged = summary.Repositories.Count(item => item.Status == RepositoryStatus.Merged),
                noChanges = summary.Repositories.Count(item => item.Status == RepositoryStatus.NoChanges),
                notReady = summary.Repositories.Count(item => item.Status is
                    RepositoryStatus.PullRequestMissing or RepositoryStatus.Outdated or
                    RepositoryStatus.CiNotReady or RepositoryStatus.CiNotPassing or
                    RepositoryStatus.PullRequestNotMergeable or RepositoryStatus.PullRequestCreated or
                    RepositoryStatus.PullRequestUpdated or RepositoryStatus.PullRequestRefreshed),
                remediated = summary.Repositories.Count(item => item.Status is
                    RepositoryStatus.PullRequestCreated or RepositoryStatus.PullRequestUpdated or
                    RepositoryStatus.PullRequestRefreshed),
                blocked = summary.Repositories.Count(item => item.Status == RepositoryStatus.Blocked),
                failed = summary.Repositories.Count(item => item.Status == RepositoryStatus.Failed)
            },
            repositories = summary.Repositories.Select(repository => new
            {
                repository.Repository,
                repository.Target,
                repository.Status,
                repository.Reason,
                repository.Detail,
                changes = repository.Operations.Select(operation => new { operation.Path, operation.Kind }),
                pullRequest = repository.PullRequest,
                error = repository.Error
            }),
            diagnostics = summary.Diagnostics
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, Options));
    }

    public void WriteCancellation()
    {
        Console.Out.WriteLine("{\"schemaVersion\":1,\"result\":\"cancelled\"}");
    }
}
