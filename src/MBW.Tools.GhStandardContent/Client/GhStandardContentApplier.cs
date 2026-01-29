using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MBW.Tools.GhStandardContent.Helpers;
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

    protected override async Task ApplyFiles(string repoFullName, Dictionary<string, byte[]> files)
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

        Reference headReference = await _client.Git.Reference.Get(_repo.Id, $"heads/{_repo.DefaultBranch}");
        string headCommit = headReference.Object.Sha;

        TreeResponse previousTree = await _client.Git.Tree.Get(_repo.Id, headReference.Ref);
        newTree.BaseTree = previousTree.Sha;

        TreeResponse newTreeResponse = await _client.Git.Tree.Create(_repo.Id, newTree);

        Commit createdCommit = await _client.Git.Commit.Create(_repo.Id,
            new NewCommit("Updating standard content files for repository", newTreeResponse.Sha, headCommit)
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

            string[] prLabels = _arguments.PrLabels.ToArray();
            if (prLabels is { Length: > 0 })
            {
                Log.Debug("Adding labels {Labels} to pr {PrNumber}", _arguments.PrLabels, newPr.Number);
                await _client.Issue.Labels.AddToIssue(_repo.Id, newPr.Number, prLabels);
            }

            Log.Information("{Repository}: PR created #{PrNumber} - {Title}", _repo.FullName, newPr.Number,
                newPr.Title);
        }
    }

    // Local overrides are fetched via FetchFiles when requested.
}
