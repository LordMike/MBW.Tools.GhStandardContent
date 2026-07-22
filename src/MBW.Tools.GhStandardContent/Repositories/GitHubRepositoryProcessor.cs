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
            GitHubState defaultState = await LoadStateAsync(repository, repository.DefaultBranch, desired, cancellationToken);
            DesiredRepository merged = _planner.ApplyLocalOverrides(desired, defaultState.Files);
            ContentPlan defaultPlan = _planner.Plan(merged, defaultState.Files, options.OrphanPolicy);
            PullRequest? openPullRequest = await FindOpenPullRequestAsync(repository, options.BranchName, cancellationToken);
            GitHubState? branchState = await TryLoadStateAsync(repository, options.BranchName, desired, cancellationToken);
            ContentPlan? branchPlan = openPullRequest is not null && branchState is not null
                ? _planner.Plan(merged, branchState.Files, options.OrphanPolicy)
                : null;

            if (openPullRequest is not null && branchPlan is { IsBlocked: false, HasChanges: false })
            {
                if (options.Mode == RunMode.Apply)
                    await AddMissingLabelsAsync(repository, openPullRequest, options.Labels, cancellationToken);
                return new RepositoryResult(desired.FullName, "github", RepositoryStatus.PullRequestOpen,
                    defaultPlan.Operations, ToInfo(openPullRequest, false));
            }

            if (options.Mode == RunMode.Check)
            {
                if (defaultPlan.IsBlocked)
                    return Blocked(desired, defaultPlan);
                if (!defaultPlan.HasChanges)
                    return new RepositoryResult(desired.FullName, "github", RepositoryStatus.UpToDate, []);

                PullRequestInfo? pr = openPullRequest is null ? null : ToInfo(openPullRequest, false);
                RepositoryStatus status = pr is null ? RepositoryStatus.ChangesPending : RepositoryStatus.PullRequestOpen;
                return new RepositoryResult(desired.FullName, "github", status, defaultPlan.Operations, pr);
            }

            if (defaultPlan.IsBlocked)
                return Blocked(desired, defaultPlan);

            if (!defaultPlan.HasChanges)
                return new RepositoryResult(desired.FullName, "github", RepositoryStatus.UpToDate, []);

            Reference latest = await ExecuteApiAsync(
                () => _client.Git.Reference.Get(repository.Id, $"heads/{repository.DefaultBranch}"), cancellationToken);
            if (!latest.Object.Sha.Equals(defaultState.CommitSha, StringComparison.Ordinal))
                continue;

            PullRequestInfo pullRequest = await ApplyAsync(
                repository, defaultState, defaultPlan.Operations, openPullRequest, options, cancellationToken);
            return new RepositoryResult(desired.FullName, "github", RepositoryStatus.Applied,
                defaultPlan.Operations, pullRequest);
        }

        throw new InvalidOperationException(
            $"Default branch for '{desired.FullName}' changed repeatedly while applying content; rerun the command.");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _apiGate.Dispose();
    }

    private static RepositoryResult Blocked(DesiredRepository desired, ContentPlan plan) =>
        new(desired.FullName, "github", RepositoryStatus.Blocked, [], null,
            new RepositoryError("orphanPolicyRequired", plan.BlockReason ?? "An orphan policy is required."));

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
        return ToInfo(pullRequest, created);
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
        string[] missing = labels.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(label => pullRequest.Labels.All(existing =>
                !existing.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missing.Length == 0)
            return;

        await ExecuteApiAsync(() => _client.Issue.Labels.AddToIssue(repository.Id, pullRequest.Number, missing),
            cancellationToken);
    }

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

    private static PullRequestInfo ToInfo(PullRequest pullRequest, bool created) =>
        new(pullRequest.Number, pullRequest.HtmlUrl, created);

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");

    private sealed record GitHubState(
        string CommitSha,
        string TreeSha,
        IReadOnlyDictionary<string, byte[]> Files);
}
