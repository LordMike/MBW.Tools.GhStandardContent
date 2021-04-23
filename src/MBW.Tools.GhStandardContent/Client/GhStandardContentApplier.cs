using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MBW.Tools.GhStandardContent.Helpers;
using Octokit;
using Serilog;

namespace MBW.Tools.GhStandardContent.Client
{
    class GhStandardContentApplier
    {
        private readonly string _branchNameRef;
        private readonly IGitHubClient _client;
        private readonly CommandlineArgs _arguments;

        public GhStandardContentApplier(IGitHubClient client, CommandlineArgs arguments)
        {
            _client = client;
            _arguments = arguments;
            _branchNameRef = $"refs/heads/{arguments.BranchName}";
        }

        public async Task Apply(string owner, string repository, GhStandardFileSet fileSet)
        {
            Repository repo = await _client.Repository.Get(owner, repository);

            // Determine if auto-content branch exists
            string checkBranch = repo.DefaultBranch;
            var branchName = _arguments.BranchName;
            try
            {
                await _client.Repository.Branch.Get(repo.Id, branchName);
                checkBranch = branchName;

                Log.Information("{Repository}: branch '{BranchName}' exists, using that as base instead of '{DefaultBranch}'", repo.FullName, branchName, repo.DefaultBranch);
            }
            catch (NotFoundException)
            {
            }

            // Diff files
            List<string> upToDate = new List<string>();
            Dictionary<string, byte[]> files = fileSet.GetFiles().ToDictionary(s => s.path, s => s.value);

            {
                foreach ((string path, byte[] value) in files)
                {
                    try
                    {
                        byte[] existing = await _client.Repository.Content.GetRawContentByRef(owner, repository, path, checkBranch);
                        Utility.NormalizeNewlines(ref existing);

                        if (existing.SequenceEqual(value))
                            upToDate.Add(path);
                    }
                    catch (NotFoundException)
                    {
                    }
                }

                if (upToDate.Count == files.Count)
                {
                    Log.Information("{Repository}: is up-to-date", repo.FullName);
                    return;
                }
            }

            foreach (string path in files.Keys.Except(upToDate))
                Log.Information("{Repository}: '{path}' is outdated", repo.FullName, path);

            NewTree newTree = new NewTree();

            foreach ((string path, byte[] value) in files)
            {
                BlobReference newBlob = await _client.Git.Blob.Create(repo.Id, new NewBlob
                {
                    Content = Convert.ToBase64String(value),
                    Encoding = EncodingType.Base64
                });

                newTree.Tree.Add(new NewTreeItem
                {
                    Type = TreeType.Blob,
                    Mode = "100644",
                    Path = path,
                    Sha = newBlob.Sha
                });
            }

            Reference headReference = await _client.Git.Reference.Get(repo.Id, $"heads/{repo.DefaultBranch}");
            string headCommit = headReference.Object.Sha;

            TreeResponse previousTree = await _client.Git.Tree.Get(repo.Id, headReference.Ref);
            newTree.BaseTree = previousTree.Sha;

            TreeResponse newTreeResponse = await _client.Git.Tree.Create(repo.Id, newTree);
            Commit createdCommit = await _client.Git.Commit.Create(repo.Id, new NewCommit("Updating standard content files for repository", newTreeResponse.Sha, headCommit)
            {
                Author = new Committer(_arguments.CommitAuthor, _arguments.CommitEmail, DateTimeOffset.UtcNow)
            });

            Reference existingBranch = null;
            try
            {
                existingBranch = await _client.Git.Reference.Get(repo.Id, _branchNameRef);
            }
            catch (NotFoundException)
            {
            }

            if (existingBranch != null)
            {
                Log.Information("{Repository}: Force-pushing to '{BranchName}'", repo.FullName, branchName);

                // Update / force-push
                await _client.Git.Reference.Update(repo.Id, _branchNameRef, new ReferenceUpdate(createdCommit.Sha, true));
            }
            else
            {
                Log.Information("{Repository}: Creating '{BranchName}'", repo.FullName, branchName);

                // Create
                await _client.Git.Reference.Create(repo.Id, new NewReference(_branchNameRef, createdCommit.Sha));

                // Create PR
                PullRequest newPr = await _client.Repository.PullRequest.Create(repo.Id, new NewPullRequest("Auto: Updating standardized files", branchName, repo.DefaultBranch));

                Log.Information("{Repository}: PR created #{PrNumber} - {Title}", repo.FullName, newPr.Number, newPr.Title);
            }
        }
    }
}