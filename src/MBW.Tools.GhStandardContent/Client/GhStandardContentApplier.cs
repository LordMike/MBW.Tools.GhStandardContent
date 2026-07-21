using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using MBW.Tools.GhStandardContent.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Octokit;
using Serilog;

namespace MBW.Tools.GhStandardContent.Client;

class GhStandardContentApplier : BaseContentApplier
{
    private readonly string _branchNameRef;
    private readonly IGitHubClient _client;
    private readonly CommandlineArgs _arguments;
    private Repository _repo;
    private string _checkBranch;

    public GhStandardContentApplier(IGitHubClient client, CommandlineArgs arguments)
    {
        _client = client;
        _arguments = arguments;
        _branchNameRef = $"refs/heads/{arguments.BranchName}";
    }

    private async Task EnsureRepo(string repoFullName)
    {
        if (_repo != null)
            return;

        try
        {
            string[] parts = repoFullName.Split('/');
            string owner = parts[0];
            string repoName = parts[1];

            _repo = await _client.Repository.Get(owner, repoName);

            // Determine if auto-content branch exists
            _checkBranch = _repo.DefaultBranch;
            try
            {
                await _client.Repository.Branch.Get(_repo.Id, _arguments.BranchName);
                _checkBranch = _arguments.BranchName;

                Log.Information(
                    "{Repository}: branch '{BranchName}' exists, using that as base instead of '{DefaultBranch}'",
                    _repo.FullName, _arguments.BranchName, _repo.DefaultBranch);
            }
            catch (NotFoundException)
            {
            }
        }
        catch (NotFoundException e)
        {
            throw new Exception(
                "Unable to interact with Github - perhaps the token used does not have the proper scopes? (ensure you have 'repo' and 'workflow')",
                e);
        }
    }

    protected override async Task<Dictionary<string, byte[]>> FetchFiles(string repoFullName, IEnumerable<string> paths)
    {
        await EnsureRepo(repoFullName);

        var results = await ParallelQueue.RunParallel(async (path, _) =>
        {
            try
            {
                byte[] existing = await _client.Repository.Content.GetRawContentByRef(_repo.Owner.Login, _repo.Name, path, _checkBranch);
                Utility.NormalizeNewlines(ref existing);
                return KeyValuePair.Create(path, existing);
            }
            catch (NotFoundException)
            {
                return KeyValuePair.Create(path, (byte[])null);
            }
        }, paths);

        return results.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal);
    }

    protected override async Task ApplyFiles(string repoFullName, Dictionary<string, byte[]> files, IReadOnlyCollection<string> removals)
    {
        await EnsureRepo(repoFullName);

        NewTree newTree = new NewTree();

        IEnumerable<NewTreeItem> newTreeItems = await ParallelQueue.RunParallel(async (input, _) =>
        {
            string path = input.Key;
            byte[] value = input.Value;

            BlobReference newBlob = await _client.Git.Blob.Create(_repo.Id, new NewBlob
            {
                Content = Convert.ToBase64String(value),
                Encoding = EncodingType.Base64
            });

            return new NewTreeItem
            {
                Type = TreeType.Blob,
                Mode = "100644",
                Path = path,
                Sha = newBlob.Sha
            };
        }, files);

        foreach (NewTreeItem newTreeItem in newTreeItems)
            newTree.Tree.Add(newTreeItem);

        if (removals.Count > 0)
        {
            foreach (string path in removals.Distinct(StringComparer.Ordinal))
            {
                newTree.Tree.Add(new NewTreeItem
                {
                    Type = TreeType.Blob,
                    Mode = "100644",
                    Path = path,
                    Sha = null
                });
            }
        }

        Reference headReference = await _client.Git.Reference.Get(_repo.Id, $"heads/{_repo.DefaultBranch}");
        string headCommit = headReference.Object.Sha;

        TreeResponse previousTree = await _client.Git.Tree.Get(_repo.Id, headReference.Ref);
        newTree.BaseTree = previousTree.Sha;

        string newTreeSha = await CreateTree(newTree);

        Commit createdCommit = await _client.Git.Commit.Create(_repo.Id,
            new NewCommit("Updating standard content files for repository", newTreeSha, headCommit)
            {
                Author = new Committer(_arguments.CommitAuthor, _arguments.CommitEmail, DateTimeOffset.UtcNow)
            });

        Reference existingBranch = null;
        try
        {
            existingBranch = await _client.Git.Reference.Get(_repo.Id, _branchNameRef);
        }
        catch (NotFoundException)
        {
        }

        if (existingBranch != null)
        {
            Log.Information("{Repository}: Force-pushing to '{BranchName}'", _repo.FullName, _arguments.BranchName);

            // Update / force-push
            await _client.Git.Reference.Update(_repo.Id, _branchNameRef,
                new ReferenceUpdate(createdCommit.Sha, true));
        }
        else
        {
            Log.Information("{Repository}: Creating '{BranchName}'", _repo.FullName, _arguments.BranchName);

            // Create
            await _client.Git.Reference.Create(_repo.Id, new NewReference(_branchNameRef, createdCommit.Sha));

            // Create PR
            PullRequest newPr = await _client.Repository.PullRequest.Create(_repo.Id,
                new NewPullRequest("Auto: Updating standardized files", _arguments.BranchName, _repo.DefaultBranch));

            string[] prLabels = (_arguments.PrLabels ?? Enumerable.Empty<string>()).ToArray();
            if (prLabels is { Length: > 0 })
            {
                Log.Debug("Adding labels {Labels} to pr {PrNumber}", _arguments.PrLabels, newPr.Number);
                await _client.Issue.Labels.AddToIssue(_repo.Id, newPr.Number, prLabels);
            }

            Log.Information("{Repository}: PR created #{PrNumber} - {Title}", _repo.FullName, newPr.Number,
                newPr.Title);
        }
    }

    private async Task<string> CreateTree(NewTree newTree)
    {
        JObject requestBody = BuildCreateTreeRequest(newTree);

        using HttpClientHandler handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(_arguments.ProxyUrl))
            handler.Proxy = new WebProxy(_arguments.ProxyUrl);

        using HttpClient httpClient = new HttpClient(handler);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(_client.Connection.BaseAddress,
                $"repos/{Uri.EscapeDataString(_repo.Owner.Login)}/{Uri.EscapeDataString(_repo.Name)}/git/trees"));

        request.Headers.UserAgent.ParseAdd("mbwarez-sc-client");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _arguments.GithubToken);
        request.Content = new StringContent(requestBody.ToString(Formatting.None), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await httpClient.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub create-tree request failed with HTTP {(int)response.StatusCode}: {responseBody}",
                null, response.StatusCode);

        JObject responseJson = JObject.Parse(responseBody);
        return responseJson.Value<string>("sha")
               ?? throw new InvalidOperationException("GitHub create-tree response did not contain a SHA");
    }

    private static JObject BuildCreateTreeRequest(NewTree newTree)
    {
        JArray treeItems = new JArray();
        foreach (NewTreeItem item in newTree.Tree)
        {
            JObject treeItem = new JObject
            {
                ["path"] = item.Path,
                ["mode"] = item.Mode,
                ["type"] = item.Type.ToString().ToLowerInvariant()
            };

            if (item.Sha != null && item.Content != null)
                throw new InvalidOperationException($"Tree item '{item.Path}' has both a SHA and content");

            if (item.Sha != null)
                treeItem["sha"] = item.Sha;
            else if (item.Content != null)
                treeItem["content"] = item.Content;
            else
                // Octokit 7.x serializes a null NewTreeItem.Sha as an empty string. GitHub requires
                // an explicit JSON null to delete an entry, so this request is serialized manually.
                // Tracked upstream:
                // https://github.com/octokit/dotnet-sdk/issues/120
                // https://github.com/octokit/octokit.net/pull/3040
                treeItem["sha"] = JValue.CreateNull();

            treeItems.Add(treeItem);
        }

        JObject requestBody = new JObject
        {
            ["tree"] = treeItems
        };
        if (newTree.BaseTree != null)
            requestBody["base_tree"] = newTree.BaseTree;

        return requestBody;
    }

    // Local overrides are fetched via FetchFiles when requested.
}
