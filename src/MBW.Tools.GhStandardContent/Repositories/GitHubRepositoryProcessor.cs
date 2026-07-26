using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MBW.Tools.GhStandardContent.Core;
using Octokit;
using Octokit.Internal;

namespace MBW.Tools.GhStandardContent.Repositories;

internal sealed class GitHubRepositoryProcessor : IRepositoryProcessor, IDisposable
{
    private const string ClientName = "mbwarez-standard-content";
    private readonly Uri _api;
    private readonly ContentPlanner _planner;
    private readonly IGitHubClient _client;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _apiGate = new(8, 8);

    public GitHubRepositoryProcessor(Uri api, Uri? proxy, string token, ContentPlanner planner)
    {
        _api = EnsureTrailingSlash(api);
        _planner = planner;

        IWebProxy? webProxy = proxy is null ? null : new WebProxy(proxy);
        _client = new GitHubClient(new Connection(
            new Octokit.ProductHeaderValue(ClientName),
            _api,
            new InMemoryCredentialStore(new Credentials(token)),
            new HttpClientAdapter(() => new HttpClientHandler { Proxy = webProxy }),
            new SimpleJsonSerializer()));

        HttpClientHandler handler = new() { Proxy = webProxy };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(ClientName);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<RepositoryResult> ProcessAsync(
        DesiredRepository desired, RunOptions options, CancellationToken cancellationToken)
    {
        string[] parts = desired.FullName.Split('/');
        Repository repository;
        try
        {
            repository = await ExecuteApiAsync(() => _client.Repository.Get(parts[0], parts[1]), cancellationToken);
        }
        catch (NotFoundException exception)
        {
            throw new InvalidOperationException(
                $"Repository '{desired.FullName}' was not found or the token cannot access it.", exception);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            GitHubAssessment assessment = await AssessAsync(repository, desired, options, cancellationToken);
            if (options.Mode == RunMode.Check)
                return CheckResult(assessment.Assessment);

            if (options.Mode == RunMode.Apply)
            {
                RepositoryResult? applied = await ApplyAssessmentAsync(
                    assessment, options, cancellationToken);
                if (applied is not null)
                    return applied;
                continue;
            }

            if (options.Mode == RunMode.Merge)
            {
                if (options.AllowUpdating &&
                    assessment.Assessment.Status is
                        RepositoryAssessmentStatus.PullRequestMissing or RepositoryAssessmentStatus.PullRequestOutdated)
                {
                    RepositoryResult? applied = await ApplyAssessmentAsync(
                        assessment, options, cancellationToken);
                    if (applied is not null)
                        return applied;
                    continue;
                }

                return await MergeAssessmentAsync(assessment, cancellationToken);
            }

            throw new ArgumentOutOfRangeException(nameof(options.Mode));
        }

        throw new InvalidOperationException(
            $"Default branch for '{desired.FullName}' changed repeatedly while applying content; rerun the command.");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _apiGate.Dispose();
    }

    private async Task<GitHubAssessment> AssessAsync(
        Repository repository,
        DesiredRepository desired,
        RunOptions options,
        CancellationToken cancellationToken)
    {
        GitHubState defaultState = await LoadStateAsync(
            repository, repository.DefaultBranch, desired, cancellationToken);
        DesiredRepository merged = _planner.ApplyLocalOverrides(desired, defaultState.Files);
        ContentPlan defaultPlan = _planner.Plan(merged, defaultState.Files, options.OrphanPolicy);

        if (defaultPlan.IsBlocked)
        {
            RepositoryAssessment blocked = new(
                desired.FullName, "github", RepositoryAssessmentStatus.Blocked, [], null, [],
                BaseSha: defaultState.CommitSha,
                Error: new RepositoryError(
                    "orphanPolicyRequired", defaultPlan.BlockReason ?? "An orphan policy is required."));
            return new GitHubAssessment(blocked, repository, defaultState, null, null);
        }

        if (!defaultPlan.HasChanges)
        {
            RepositoryAssessment noChanges = new(
                desired.FullName, "github", RepositoryAssessmentStatus.NoChanges, [], null, [],
                BaseSha: defaultState.CommitSha);
            return new GitHubAssessment(noChanges, repository, defaultState, null, null);
        }

        PullRequest? pullRequest = await FindOpenPullRequestAsync(
            repository, options.BranchName, cancellationToken);
        if (pullRequest is null)
        {
            RepositoryAssessment missing = new(
                desired.FullName, "github", RepositoryAssessmentStatus.PullRequestMissing,
                defaultPlan.Operations, null, [], BaseSha: defaultState.CommitSha);
            return new GitHubAssessment(missing, repository, defaultState, null, null);
        }

        GitHubState? branchState = await TryLoadStateAsync(
            repository, options.BranchName, desired, cancellationToken);
        List<PullRequestIssue> issues = [];
        CompareResult? comparison = null;
        if (branchState is null)
        {
            issues.Add(PullRequestIssue.ContentMismatch);
        }
        else
        {
            ContentPlan branchPlan = _planner.Plan(merged, branchState.Files, options.OrphanPolicy);
            if (branchPlan.IsBlocked || branchPlan.HasChanges)
                issues.Add(PullRequestIssue.ContentMismatch);

            comparison = await ExecuteApiAsync(
                () => _client.Repository.Commit.Compare(
                    repository.Id, defaultState.CommitSha, branchState.CommitSha), cancellationToken);
            IEnumerable<string> changedPaths = comparison.Files.SelectMany(file =>
                file.Status.Equals("renamed", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(file.PreviousFileName)
                    ? new[] { file.PreviousFileName, file.Filename }
                    : [file.Filename]);
            if (!HasExactDiff(defaultPlan.Operations, changedPaths))
                issues.Add(PullRequestIssue.UnexpectedChanges);
            if (comparison.BehindBy > 0)
                issues.Add(PullRequestIssue.BehindBase);
        }

        string[] missingLabels = MissingLabels(pullRequest, options.Labels);
        if (missingLabels.Length > 0)
            issues.Add(PullRequestIssue.LabelsMissing);

        PullRequestInfo info = ToInfo(
            pullRequest, false, comparison?.BehindBy ?? 0, branchState?.CommitSha ?? pullRequest.Head.Sha);
        RepositoryAssessmentStatus status = issues.Count == 0
            ? RepositoryAssessmentStatus.PullRequestCurrent
            : RepositoryAssessmentStatus.PullRequestOutdated;
        RepositoryAssessment assessment = new(
            desired.FullName, "github", status, defaultPlan.Operations, info, issues,
            defaultState.CommitSha, branchState?.CommitSha ?? pullRequest.Head.Sha);
        return new GitHubAssessment(assessment, repository, defaultState, branchState, pullRequest);
    }

    private static RepositoryResult CheckResult(RepositoryAssessment assessment) =>
        assessment.Status switch
        {
            RepositoryAssessmentStatus.Blocked => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.Blocked, [], null, assessment.Error),
            RepositoryAssessmentStatus.NoChanges => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.UpToDate, []),
            RepositoryAssessmentStatus.PullRequestMissing => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.ChangesPending, assessment.Operations),
            RepositoryAssessmentStatus.PullRequestCurrent => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.PullRequestOpen,
                assessment.Operations, assessment.PullRequest),
            RepositoryAssessmentStatus.PullRequestOutdated when
                assessment.Issues.SequenceEqual([PullRequestIssue.BehindBase]) => new RepositoryResult(
                    assessment.Repository, assessment.Target, RepositoryStatus.PullRequestBehind,
                    assessment.Operations, assessment.PullRequest,
                    Reason: assessment.PrimaryReason, Detail: assessment.IssueDetail),
            RepositoryAssessmentStatus.PullRequestOutdated => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.PullRequestOpen,
                assessment.Operations, assessment.PullRequest,
                Reason: assessment.PrimaryReason, Detail: assessment.IssueDetail),
            _ => throw new ArgumentOutOfRangeException(nameof(assessment))
        };

    private async Task<RepositoryResult?> ApplyAssessmentAsync(
        GitHubAssessment state,
        RunOptions options,
        CancellationToken cancellationToken)
    {
        RepositoryAssessment assessment = state.Assessment;
        if (assessment.Status == RepositoryAssessmentStatus.Blocked)
            return new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.Blocked, [], null, assessment.Error);
        if (assessment.Status == RepositoryAssessmentStatus.NoChanges)
            return new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.UpToDate, []);
        if (assessment.Status == RepositoryAssessmentStatus.PullRequestCurrent)
            return new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.PullRequestOpen,
                assessment.Operations, assessment.PullRequest);

        bool labelsOnly = assessment.Status == RepositoryAssessmentStatus.PullRequestOutdated &&
                          assessment.Issues.All(issue => issue == PullRequestIssue.LabelsMissing);
        if (labelsOnly)
        {
            await AddMissingLabelsAsync(
                state.Repository, state.PullRequest!, options.Labels, cancellationToken);
            return new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.PullRequestUpdated,
                assessment.Operations, assessment.PullRequest,
                Reason: RepositoryReason.LabelsMissing, Detail: assessment.IssueDetail);
        }

        Reference latest = await ExecuteApiAsync(
            () => _client.Git.Reference.Get(
                state.Repository.Id, $"heads/{state.Repository.DefaultBranch}"), cancellationToken);
        if (!latest.Object.Sha.Equals(assessment.BaseSha, StringComparison.Ordinal))
            return null;

        PullRequestInfo pullRequest = await ApplyAsync(
            state.Repository, state.DefaultState, assessment.Operations,
            state.PullRequest, options, cancellationToken);
        RepositoryStatus status;
        if (pullRequest.Created)
            status = RepositoryStatus.PullRequestCreated;
        else if (assessment.Issues.SequenceEqual([PullRequestIssue.BehindBase]))
            status = RepositoryStatus.PullRequestRefreshed;
        else
            status = RepositoryStatus.PullRequestUpdated;

        return new RepositoryResult(
            assessment.Repository, assessment.Target, status, assessment.Operations,
            pullRequest with { BehindBy = assessment.PullRequest?.BehindBy ?? 0 },
            Reason: assessment.PrimaryReason, Detail: assessment.IssueDetail);
    }

    private async Task<GitHubState> LoadStateAsync(
        Repository repository, string branch, DesiredRepository desired, CancellationToken cancellationToken)
    {
        Reference reference = await ExecuteApiAsync(
            () => _client.Git.Reference.Get(repository.Id, $"heads/{branch}"), cancellationToken);
        Commit commit = await ExecuteApiAsync(
            () => _client.Git.Commit.Get(repository.Id, reference.Object.Sha), cancellationToken);

        Dictionary<string, byte[]> files = await FetchFilesAsync(repository, reference.Object.Sha,
            _planner.InitialFetchPaths(desired), cancellationToken);
        files = await FetchFilesAsync(repository, reference.Object.Sha,
            _planner.ExpandFetchPaths(desired, files), cancellationToken);
        return new GitHubState(reference.Object.Sha, commit.Tree.Sha, files);
    }

    private async Task<GitHubState?> TryLoadStateAsync(
        Repository repository, string branch, DesiredRepository desired, CancellationToken cancellationToken)
    {
        try
        {
            return await LoadStateAsync(repository, branch, desired, cancellationToken);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private async Task<Dictionary<string, byte[]>> FetchFilesAsync(
        Repository repository, string reference, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        ConcurrentDictionary<string, byte[]> files = new(StringComparer.Ordinal);
        await Parallel.ForEachAsync(paths.Distinct(StringComparer.Ordinal),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (path, token) =>
            {
                try
                {
                    byte[] content = await ExecuteApiAsync(
                        () => _client.Repository.Content.GetRawContentByRef(
                            repository.Owner.Login, repository.Name, path, reference), token);
                    files[path] = content;
                }
                catch (NotFoundException)
                {
                    // Absence is part of the repository snapshot.
                }
            });
        return new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
    }

    private async Task<PullRequest?> FindOpenPullRequestAsync(
        Repository repository, string branch, CancellationToken cancellationToken)
    {
        PullRequestRequest request = new()
        {
            State = ItemStateFilter.Open,
            Head = $"{repository.Owner.Login}:{branch}",
            Base = repository.DefaultBranch
        };
        IReadOnlyList<PullRequest> pullRequests = await ExecuteApiAsync(
            () => _client.PullRequest.GetAllForRepository(repository.Id, request), cancellationToken);
        return pullRequests.FirstOrDefault();
    }

    private async Task<RepositoryResult> MergeAssessmentAsync(
        GitHubAssessment state, CancellationToken cancellationToken)
    {
        RepositoryAssessment assessment = state.Assessment;
        if (assessment.Status == RepositoryAssessmentStatus.Blocked)
            return new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.Blocked, [], null, assessment.Error);
        if (assessment.Status == RepositoryAssessmentStatus.NoChanges)
            return new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.NoChanges, []);
        if (assessment.Status == RepositoryAssessmentStatus.PullRequestMissing)
            return new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.PullRequestMissing,
                assessment.Operations, Reason: null, Detail: "Run apply or use --allow-updating.");
        if (assessment.Status == RepositoryAssessmentStatus.PullRequestOutdated)
            return new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.Outdated,
                assessment.Operations, assessment.PullRequest,
                Reason: assessment.PrimaryReason, Detail: assessment.IssueDetail);
        if (assessment.Status != RepositoryAssessmentStatus.PullRequestCurrent)
            throw new ArgumentOutOfRangeException(nameof(assessment));

        MergeEligibility eligibility = await LoadMergeEligibilityAsync(
            state.Repository, state.PullRequest!, cancellationToken);
        if (!eligibility.HeadSha.Equals(assessment.HeadSha, StringComparison.Ordinal))
            return OutdatedHead(assessment);

        RepositoryResult? ciResult = CiResult(assessment, eligibility);
        if (ciResult is not null)
            return ciResult;

        if (state.Repository.AllowSquashMerge == false)
            return NotMergeable(
                assessment, RepositoryReason.SquashUnavailable, "Squash merging is disabled.");

        RepositoryResult? mergeStateResult = MergeStateResult(assessment, eligibility);
        if (mergeStateResult is not null)
            return mergeStateResult;

        Reference latestDefault = await ExecuteApiAsync(
            () => _client.Git.Reference.Get(
                state.Repository.Id, $"heads/{state.Repository.DefaultBranch}"), cancellationToken);
        if (!latestDefault.Object.Sha.Equals(assessment.BaseSha, StringComparison.Ordinal))
            return OutdatedHead(assessment);

        PullRequestMerge merged;
        try
        {
            merged = await ExecuteApiAsync(
                () => _client.PullRequest.Merge(
                    state.Repository.Id,
                    state.PullRequest!.Number,
                    new MergePullRequest
                    {
                        MergeMethod = PullRequestMergeMethod.Squash,
                        Sha = assessment.HeadSha
                    }),
                cancellationToken);
        }
        catch (ApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            return OutdatedHead(assessment);
        }
        catch (ApiException exception) when (exception.StatusCode is
                                                 HttpStatusCode.MethodNotAllowed or
                                                 HttpStatusCode.UnprocessableEntity)
        {
            return NotMergeable(
                assessment, RepositoryReason.MergeRejected, exception.Message);
        }

        if (!merged.Merged)
            return NotMergeable(
                assessment, RepositoryReason.MergeRejected, merged.Message);

        PullRequestInfo pullRequest = assessment.PullRequest! with
        {
            HeadSha = assessment.HeadSha,
            MergeCommitSha = merged.Sha
        };
        return new RepositoryResult(
            assessment.Repository, assessment.Target, RepositoryStatus.Merged,
            assessment.Operations, pullRequest);
    }

    internal static bool HasExactDiff(
        IReadOnlyList<FileOperation> operations, IEnumerable<string> actualPaths)
    {
        HashSet<string> expected = operations
            .Select(operation => operation.Path)
            .ToHashSet(StringComparer.Ordinal);
        return expected.SetEquals(actualPaths);
    }

    internal static RepositoryResult? CiResult(
        RepositoryAssessment assessment, MergeEligibility eligibility)
    {
        if (eligibility.CiState is null ||
            eligibility.CiState.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) &&
            eligibility.CiTotal == 0)
        {
            return new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.CiNotReady,
                assessment.Operations, assessment.PullRequest,
                Reason: RepositoryReason.NoResults, Detail: "No CI results were reported.");
        }

        return eligibility.CiState.ToUpperInvariant() switch
        {
            "EXPECTED" => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.CiNotReady,
                assessment.Operations, assessment.PullRequest,
                Reason: RepositoryReason.Expected, Detail: "CI is expected but has not reported yet."),
            "PENDING" => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.CiNotReady,
                assessment.Operations, assessment.PullRequest,
                Reason: RepositoryReason.Pending, Detail: "CI is still running."),
            "FAILURE" => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.CiNotPassing,
                assessment.Operations, assessment.PullRequest,
                Reason: RepositoryReason.Failure, Detail: "CI reported a failure."),
            "ERROR" => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.CiNotPassing,
                assessment.Operations, assessment.PullRequest,
                Reason: RepositoryReason.Error, Detail: "CI reported an error."),
            "SUCCESS" => null,
            _ => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.CiNotReady,
                assessment.Operations, assessment.PullRequest,
                Reason: RepositoryReason.Pending,
                Detail: $"CI state is '{eligibility.CiState}'.")
        };
    }

    internal static RepositoryResult? MergeStateResult(
        RepositoryAssessment assessment, MergeEligibility eligibility) =>
        eligibility.MergeState.ToUpperInvariant() switch
        {
            "CLEAN" or "HAS_HOOKS" => null,
            "BEHIND" => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.Outdated,
                assessment.Operations, assessment.PullRequest,
                Reason: RepositoryReason.BehindBase,
                Detail: "The PR head is behind the default branch."),
            "UNSTABLE" => new RepositoryResult(
                assessment.Repository, assessment.Target, RepositoryStatus.CiNotPassing,
                assessment.Operations, assessment.PullRequest,
                Reason: RepositoryReason.Failure,
                Detail: "GitHub reports a non-passing commit status."),
            "DRAFT" => NotMergeable(assessment, RepositoryReason.Draft, "The PR is a draft."),
            "DIRTY" => NotMergeable(assessment, RepositoryReason.Conflicts, "The PR has merge conflicts."),
            "UNKNOWN" => NotMergeable(
                assessment, RepositoryReason.MergeabilityUnknown, "GitHub has not determined mergeability."),
            "BLOCKED" when eligibility.ReviewDecision?.Equals(
                "REVIEW_REQUIRED", StringComparison.OrdinalIgnoreCase) == true =>
                NotMergeable(assessment, RepositoryReason.ReviewRequired, "A review is required."),
            "BLOCKED" when eligibility.ReviewDecision?.Equals(
                "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase) == true =>
                NotMergeable(assessment, RepositoryReason.ChangesRequested, "Changes were requested."),
            "BLOCKED" => NotMergeable(
                assessment, RepositoryReason.PolicyBlocked, "GitHub repository policy blocks this merge."),
            _ => NotMergeable(
                assessment, RepositoryReason.MergeabilityUnknown,
                $"GitHub merge state is '{eligibility.MergeState}'.")
        };

    private static RepositoryResult NotMergeable(
        RepositoryAssessment assessment, RepositoryReason reason, string? detail) =>
        new(
            assessment.Repository, assessment.Target, RepositoryStatus.PullRequestNotMergeable,
            assessment.Operations, assessment.PullRequest,
            Reason: reason, Detail: detail);

    private static RepositoryResult OutdatedHead(RepositoryAssessment assessment) =>
        new(
            assessment.Repository, assessment.Target, RepositoryStatus.Outdated,
            assessment.Operations, assessment.PullRequest,
            Reason: RepositoryReason.HeadChanged,
            Detail: "The PR head or default branch changed after assessment.");

    private async Task<MergeEligibility> LoadMergeEligibilityAsync(
        Repository repository, PullRequest pullRequest, CancellationToken cancellationToken)
    {
        JsonObject body = new()
        {
            ["query"] = """
                query($owner: String!, $name: String!, $number: Int!) {
                  repository(owner: $owner, name: $name) {
                    pullRequest(number: $number) {
                      headRefOid
                      mergeStateStatus
                      reviewDecision
                      statusCheckRollup {
                        state
                        contexts {
                          totalCount
                        }
                      }
                    }
                  }
                }
                """,
            ["variables"] = new JsonObject
            {
                ["owner"] = repository.Owner.Login,
                ["name"] = repository.Name,
                ["number"] = pullRequest.Number
            }
        };
        using HttpRequestMessage request = new(HttpMethod.Post, BuildGraphQlEndpoint(_api))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub GraphQL request failed with HTTP {(int)response.StatusCode}: {responseBody}",
                null, response.StatusCode);

        return ParseMergeEligibility(responseBody);
    }

    internal static Uri BuildGraphQlEndpoint(Uri api)
    {
        UriBuilder builder = new(api);
        if (api.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = "/graphql";
        }
        else
        {
            string path = api.AbsolutePath.TrimEnd('/');
            builder.Path = path.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase)
                ? path[..^2] + "graphql"
                : path + "/graphql";
        }

        builder.Query = string.Empty;
        builder.Fragment = string.Empty;
        return builder.Uri;
    }

    internal static MergeEligibility ParseMergeEligibility(string responseBody)
    {
        using JsonDocument json = JsonDocument.Parse(responseBody);
        JsonElement root = json.RootElement;
        if (root.TryGetProperty("errors", out JsonElement errors) &&
            errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            string messages = string.Join("; ", errors.EnumerateArray()
                .Select(error => error.TryGetProperty("message", out JsonElement message)
                    ? message.GetString()
                    : error.ToString()));
            throw new InvalidOperationException($"GitHub GraphQL request failed: {messages}");
        }

        JsonElement pullRequest = root.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");
        if (pullRequest.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException("The pull request no longer exists.");

        string headSha = pullRequest.GetProperty("headRefOid").GetString()
                         ?? throw new InvalidOperationException("GitHub did not return the PR head SHA.");
        string mergeState = pullRequest.GetProperty("mergeStateStatus").GetString()
                            ?? throw new InvalidOperationException("GitHub did not return merge state.");
        string? reviewDecision = pullRequest.TryGetProperty("reviewDecision", out JsonElement review) &&
                                 review.ValueKind != JsonValueKind.Null
            ? review.GetString()
            : null;
        string? ciState = null;
        int ciTotal = 0;
        if (pullRequest.TryGetProperty("statusCheckRollup", out JsonElement rollup) &&
            rollup.ValueKind != JsonValueKind.Null)
        {
            ciState = rollup.GetProperty("state").GetString();
            ciTotal = rollup.GetProperty("contexts").GetProperty("totalCount").GetInt32();
        }

        return new MergeEligibility(headSha, mergeState, reviewDecision, ciState, ciTotal);
    }

    private async Task<PullRequestInfo> ApplyAsync(
        Repository repository,
        GitHubState baseState,
        IReadOnlyList<FileOperation> operations,
        PullRequest? existingPullRequest,
        RunOptions options,
        CancellationToken cancellationToken)
    {
        ConcurrentDictionary<string, string> blobShas = new(StringComparer.Ordinal);
        await Parallel.ForEachAsync(operations.Where(operation => operation.Kind != FileOperationKind.Delete),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (operation, token) =>
            {
                BlobReference blob = await ExecuteApiAsync(() => _client.Git.Blob.Create(repository.Id, new NewBlob
                {
                    Content = Convert.ToBase64String(operation.Content!),
                    Encoding = EncodingType.Base64
                }), token);
                blobShas[operation.Path] = blob.Sha;
            });

        string treeSha = await CreateTreeAsync(repository, baseState.TreeSha, operations, blobShas, cancellationToken);
        Commit commit = await ExecuteApiAsync(() => _client.Git.Commit.Create(repository.Id,
            new NewCommit("Updating standard content files for repository", treeSha, baseState.CommitSha)
            {
                Author = new Committer(options.CommitAuthor, options.CommitEmail, DateTimeOffset.UtcNow)
            }), cancellationToken);

        string branchReference = $"refs/heads/{options.BranchName}";
        bool branchExists;
        try
        {
            _ = await ExecuteApiAsync(() => _client.Git.Reference.Get(repository.Id, branchReference), cancellationToken);
            branchExists = true;
        }
        catch (NotFoundException)
        {
            branchExists = false;
        }

        if (branchExists)
        {
            await ExecuteApiAsync(() => _client.Git.Reference.Update(repository.Id, branchReference,
                new ReferenceUpdate(commit.Sha, true)), cancellationToken);
        }
        else
        {
            await ExecuteApiAsync(() => _client.Git.Reference.Create(repository.Id,
                new NewReference(branchReference, commit.Sha)), cancellationToken);
        }

        PullRequest? pullRequest = existingPullRequest ??
                                   await FindOpenPullRequestAsync(repository, options.BranchName, cancellationToken);
        bool created = false;
        if (pullRequest is null)
        {
            pullRequest = await ExecuteApiAsync(() => _client.PullRequest.Create(repository.Id,
                new NewPullRequest("Auto: Updating standardized files", options.BranchName, repository.DefaultBranch)),
                cancellationToken);
            created = true;
        }

        await AddMissingLabelsAsync(repository, pullRequest, options.Labels, cancellationToken);
        return ToInfo(pullRequest, created, headSha: commit.Sha);
    }

    private async Task<string> CreateTreeAsync(
        Repository repository,
        string baseTree,
        IReadOnlyList<FileOperation> operations,
        IReadOnlyDictionary<string, string> blobShas,
        CancellationToken cancellationToken)
    {
        JsonObject body = BuildCreateTreeBody(baseTree, operations, blobShas);
        Uri endpoint = new(_api,
            $"repos/{Uri.EscapeDataString(repository.Owner.Login)}/{Uri.EscapeDataString(repository.Name)}/git/trees");

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub create-tree request failed with HTTP {(int)response.StatusCode}: {responseBody}",
                null, response.StatusCode);

        using JsonDocument json = JsonDocument.Parse(responseBody);
        return json.RootElement.GetProperty("sha").GetString()
               ?? throw new InvalidOperationException("GitHub create-tree response did not contain a SHA.");
    }

    internal static JsonObject BuildCreateTreeBody(
        string baseTree,
        IReadOnlyList<FileOperation> operations,
        IReadOnlyDictionary<string, string> blobShas)
    {
        JsonArray items = [];
        foreach (FileOperation operation in operations)
        {
            JsonObject item = new()
            {
                ["path"] = operation.Path,
                ["mode"] = "100644",
                ["type"] = "blob",
                ["sha"] = operation.Kind == FileOperationKind.Delete ? null : blobShas[operation.Path]
            };
            items.Add(item);
        }

        return new JsonObject { ["base_tree"] = baseTree, ["tree"] = items };
    }

    private async Task AddMissingLabelsAsync(
        Repository repository, PullRequest pullRequest, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        string[] missing = MissingLabels(pullRequest, labels);
        if (missing.Length == 0)
            return;

        await ExecuteApiAsync(() => _client.Issue.Labels.AddToIssue(repository.Id, pullRequest.Number, missing),
            cancellationToken);
    }

    private static string[] MissingLabels(PullRequest pullRequest, IReadOnlyList<string> labels) =>
        labels.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(label => pullRequest.Labels.All(existing =>
                !existing.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    private async Task<T> ExecuteApiAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            await _apiGate.WaitAsync(cancellationToken);
            try
            {
                return await operation();
            }
            catch (RateLimitExceededException exception) when (attempt < 2)
            {
                TimeSpan delay = exception.Reset - DateTimeOffset.UtcNow;
                if (delay <= TimeSpan.Zero)
                    delay = TimeSpan.FromSeconds(1);
                if (delay > TimeSpan.FromSeconds(30))
                    throw;
                await Task.Delay(delay, cancellationToken);
            }
            catch (ApiException exception) when (attempt < 2 && IsTransient(exception.StatusCode))
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
            finally
            {
                _apiGate.Release();
            }
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static PullRequestInfo ToInfo(
        PullRequest pullRequest, bool created, int behindBy = 0, string? headSha = null) =>
        new(pullRequest.Number, pullRequest.HtmlUrl, created, behindBy, headSha ?? pullRequest.Head.Sha);

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    private sealed record GitHubState(
        string CommitSha,
        string TreeSha,
        IReadOnlyDictionary<string, byte[]> Files);

    private sealed record GitHubAssessment(
        RepositoryAssessment Assessment,
        Repository Repository,
        GitHubState DefaultState,
        GitHubState? BranchState,
        PullRequest? PullRequest);

    internal sealed record MergeEligibility(
        string HeadSha,
        string MergeState,
        string? ReviewDecision,
        string? CiState,
        int CiTotal);
}
