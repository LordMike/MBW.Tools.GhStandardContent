using System.Text.Json;
using MBW.Tools.GhStandardContent.Cli;
using MBW.Tools.GhStandardContent.Core;
using MBW.Tools.GhStandardContent.Reporting;
using MBW.Tools.GhStandardContent.Repositories;

namespace MBW.Tools.GhStandardContent.Tests;

[Collection("Console")]
public sealed class MergeCommandTests
{
    [Fact]
    public void MergeCommandIsGitHubOnlyAndSupportsExplicitRemediation()
    {
        var root = CliApplication.BuildRootCommand();
        var command = root.Subcommands.Single(item => item.Name == "merge");

        Assert.Contains(command.Options, option => option.Name.TrimStart('-') == "allow-updating");
        Assert.DoesNotContain(command.Options, option => option.Name.TrimStart('-') == "local");
        Assert.Empty(root.Parse(["merge", "repos.json"]).Errors);
        Assert.Empty(root.Parse(["merge", "repos.json", "--allow-updating", "-r", "owner/repo"]).Errors);
        Assert.NotEmpty(root.Parse(["merge", "repos.json", "--local", "."]).Errors);
    }

    [Fact]
    public void ExactDiffRejectsMissingAndUnexpectedPaths()
    {
        FileOperation[] operations =
        [
            new("added.txt", FileOperationKind.Add),
            new("updated.txt", FileOperationKind.Update)
        ];

        Assert.True(GitHubRepositoryProcessor.HasExactDiff(
            operations, ["updated.txt", "added.txt"]));
        Assert.False(GitHubRepositoryProcessor.HasExactDiff(
            operations, ["added.txt"]));
        Assert.False(GitHubRepositoryProcessor.HasExactDiff(
            operations, ["added.txt", "updated.txt", "unrelated.txt"]));
    }

    [Theory]
    [InlineData("https://api.github.com/", "https://api.github.com/graphql")]
    [InlineData("https://github.example/api/v3/", "https://github.example/api/graphql")]
    [InlineData("https://github.example/custom/", "https://github.example/custom/graphql")]
    public void GraphQlEndpointMatchesGitHubDeployment(string api, string expected)
    {
        Assert.Equal(new Uri(expected), GitHubRepositoryProcessor.BuildGraphQlEndpoint(new Uri(api)));
    }

    [Fact]
    public void GraphQlEligibilityParserReadsRollupAndMergeState()
    {
        GitHubRepositoryProcessor.MergeEligibility result =
            GitHubRepositoryProcessor.ParseMergeEligibility("""
                {
                  "data": {
                    "repository": {
                      "pullRequest": {
                        "headRefOid": "head-sha",
                        "mergeStateStatus": "BLOCKED",
                        "reviewDecision": "REVIEW_REQUIRED",
                        "statusCheckRollup": {
                          "state": "PENDING",
                          "contexts": { "totalCount": 3 }
                        }
                      }
                    }
                  }
                }
                """);

        Assert.Equal("head-sha", result.HeadSha);
        Assert.Equal("BLOCKED", result.MergeState);
        Assert.Equal("REVIEW_REQUIRED", result.ReviewDecision);
        Assert.Equal("PENDING", result.CiState);
        Assert.Equal(3, result.CiTotal);
    }

    [Fact]
    public void CiRollupStatesMapToCompactStatusesAndReasons()
    {
        RepositoryAssessment assessment = CurrentAssessment();
        (string? State, int Total, RepositoryStatus? Status, RepositoryReason? Reason)[] cases =
        [
            (null, 0, RepositoryStatus.CiNotReady, RepositoryReason.NoResults),
            ("EXPECTED", 0, RepositoryStatus.CiNotReady, RepositoryReason.Expected),
            ("PENDING", 2, RepositoryStatus.CiNotReady, RepositoryReason.Pending),
            ("FAILURE", 2, RepositoryStatus.CiNotPassing, RepositoryReason.Failure),
            ("ERROR", 2, RepositoryStatus.CiNotPassing, RepositoryReason.Error),
            ("SUCCESS", 0, RepositoryStatus.CiNotReady, RepositoryReason.NoResults),
            ("SUCCESS", 2, null, null)
        ];

        foreach ((string? state, int total, RepositoryStatus? status, RepositoryReason? reason) in cases)
        {
            RepositoryResult? result = GitHubRepositoryProcessor.CiResult(
                assessment,
                new GitHubRepositoryProcessor.MergeEligibility(
                    "head-sha", "CLEAN", null, state, total));

            Assert.Equal(status, result?.Status);
            Assert.Equal(reason, result?.Reason);
        }
    }

    [Fact]
    public void MergeStateConflatesGeneralBlockersButPreservesReason()
    {
        RepositoryAssessment assessment = CurrentAssessment();
        (string State, string? Review, RepositoryStatus? Status, RepositoryReason? Reason)[] cases =
        [
            ("CLEAN", null, null, null),
            ("HAS_HOOKS", null, null, null),
            ("BEHIND", null, RepositoryStatus.Outdated, RepositoryReason.BehindBase),
            ("UNSTABLE", null, RepositoryStatus.CiNotPassing, RepositoryReason.Failure),
            ("DRAFT", null, RepositoryStatus.PullRequestNotMergeable, RepositoryReason.Draft),
            ("DIRTY", null, RepositoryStatus.PullRequestNotMergeable, RepositoryReason.Conflicts),
            ("UNKNOWN", null, RepositoryStatus.PullRequestNotMergeable, RepositoryReason.MergeabilityUnknown),
            ("BLOCKED", "REVIEW_REQUIRED", RepositoryStatus.PullRequestNotMergeable, RepositoryReason.ReviewRequired),
            ("BLOCKED", "CHANGES_REQUESTED", RepositoryStatus.PullRequestNotMergeable, RepositoryReason.ChangesRequested),
            ("BLOCKED", "APPROVED", RepositoryStatus.PullRequestNotMergeable, RepositoryReason.PolicyBlocked)
        ];

        foreach ((string state, string? review, RepositoryStatus? status, RepositoryReason? reason) in cases)
        {
            RepositoryResult? result = GitHubRepositoryProcessor.MergeStateResult(
                assessment,
                new GitHubRepositoryProcessor.MergeEligibility(
                    "head-sha", state, review, "SUCCESS", 1));

            Assert.Equal(status, result?.Status);
            Assert.Equal(reason, result?.Reason);
        }
    }

    [Fact]
    public void MergeExitCodeRequiresEveryRepositoryToBeFinished()
    {
        Assert.Equal(0, Summary(RepositoryStatus.Merged, RepositoryStatus.NoChanges).ExitCode);
        Assert.Equal(2, Summary(RepositoryStatus.PullRequestCreated).ExitCode);
        Assert.Equal(2, Summary(RepositoryStatus.CiNotReady).ExitCode);
        Assert.Equal(2, Summary(RepositoryStatus.PullRequestNotMergeable).ExitCode);
        Assert.Equal(3, Summary(RepositoryStatus.Failed).ExitCode);
        Assert.Equal(3, Summary(RepositoryStatus.Merged, RepositoryStatus.Blocked).ExitCode);
    }

    [Fact]
    public void MergeReporterIncludesCompactStatusReasonAndAggregates()
    {
        TextWriter original = Console.Out;
        using StringWriter writer = new();
        Console.SetOut(writer);
        try
        {
            RunSummary summary = new(RunMode.Merge, "notReady",
            [
                new RepositoryResult(
                    "owner/repo", "github", RepositoryStatus.CiNotReady,
                    [new FileOperation("file.txt", FileOperationKind.Update)],
                    new PullRequestInfo(42, "https://example.test/pull/42", false, HeadSha: "head-sha"),
                    Reason: RepositoryReason.Expected,
                    Detail: "CI is expected but has not reported yet.")
            ], []);

            new JsonRunReporter().Write(summary);

            using JsonDocument json = JsonDocument.Parse(writer.ToString());
            Assert.Equal("merge", json.RootElement.GetProperty("command").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("notReady").GetInt32());
            Assert.Equal("ciNotReady", json.RootElement.GetProperty("repositories")[0]
                .GetProperty("status").GetString());
            Assert.Equal("expected", json.RootElement.GetProperty("repositories")[0]
                .GetProperty("reason").GetString());
            Assert.Equal("head-sha", json.RootElement.GetProperty("repositories")[0]
                .GetProperty("pullRequest").GetProperty("headSha").GetString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void TextReporterUsesMergeSpecificStatusLanguage()
    {
        TextWriter original = Console.Out;
        using StringWriter writer = new();
        Console.SetOut(writer);
        try
        {
            RunSummary summary = new(RunMode.Merge, "notReady",
            [
                new RepositoryResult("owner/merged", "github", RepositoryStatus.Merged, []),
                new RepositoryResult("owner/clean", "github", RepositoryStatus.NoChanges, []),
                new RepositoryResult("owner/missing", "github", RepositoryStatus.PullRequestMissing, []),
                new RepositoryResult("owner/outdated", "github", RepositoryStatus.Outdated, []),
                new RepositoryResult("owner/ci", "github", RepositoryStatus.CiNotPassing, []),
                new RepositoryResult(
                    "owner/blocked", "github", RepositoryStatus.PullRequestNotMergeable, [])
            ], []);

            new TextRunReporter(ColorMode.Never, OutputVerbosity.Normal).Write(summary);

            string output = writer.ToString();
            Assert.Contains("merged", output, StringComparison.Ordinal);
            Assert.Contains("no changes", output, StringComparison.Ordinal);
            Assert.Contains("PR not created", output, StringComparison.Ordinal);
            Assert.Contains("outdated", output, StringComparison.Ordinal);
            Assert.Contains("CI not passing", output, StringComparison.Ordinal);
            Assert.Contains("PR not mergeable", output, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static RepositoryAssessment CurrentAssessment() =>
        new(
            "owner/repo",
            "github",
            RepositoryAssessmentStatus.PullRequestCurrent,
            [new FileOperation("file.txt", FileOperationKind.Update)],
            new PullRequestInfo(42, "https://example.test/pull/42", false, HeadSha: "head-sha"),
            [],
            "base-sha",
            "head-sha");

    private static RunSummary Summary(params RepositoryStatus[] statuses) =>
        new(
            RunMode.Merge,
            "test",
            statuses.Select((status, index) =>
                new RepositoryResult($"owner/repo-{index}", "github", status, [])).ToArray(),
            []);
}
