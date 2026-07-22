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

    public void RepositoryStarted(string repository)
    {
    }

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
                upToDate = summary.Repositories.Count(item => item.Status == RepositoryStatus.UpToDate),
                changed = summary.Repositories.Count(item => item.Status is RepositoryStatus.Applied or RepositoryStatus.ChangesPending),
                pullRequests = summary.Repositories.Count(item => item.Status == RepositoryStatus.PullRequestOpen),
                blocked = summary.Repositories.Count(item => item.Status == RepositoryStatus.Blocked),
                failed = summary.Repositories.Count(item => item.Status == RepositoryStatus.Failed)
            },
            repositories = summary.Repositories.Select(repository => new
            {
                repository.Repository,
                repository.Target,
                repository.Status,
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