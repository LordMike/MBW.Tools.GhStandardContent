using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MBW.Tools.GhStandardContent.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Octokit;
using Serilog;

namespace MBW.Tools.GhStandardContent.Client;

class GhStandardContentApplier
{
    private static readonly HashSet<string> LocalAppendFiles = new(StringComparer.Ordinal)
    {
        ".gitignore",
        ".dockerignore"
    };

    private const string MetaFilePath = ".standard_content.json";

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
        try
        {
            Repository repo = await _client.Repository.Get(owner, repository);

            // Determine if auto-content branch exists
            string checkBranch = repo.DefaultBranch;
            string branchName = _arguments.BranchName;
            try
            {
                await _client.Repository.Branch.Get(repo.Id, branchName);
                checkBranch = branchName;

                Log.Information(
                    "{Repository}: branch '{BranchName}' exists, using that as base instead of '{DefaultBranch}'",
                    repo.FullName, branchName, repo.DefaultBranch);
            }
            catch (NotFoundException)
            {
            }

            // Diff files
            Dictionary<string, byte[]> files = fileSet.GetFiles().ToDictionary(s => s.path, s => s.value);
            await AppendLocalOverrides(owner, repository, checkBranch, files);

            {
                var outdated = await ParallelQueue.RunParallel(async (input, _) =>
                {
                    try
                    {
                        byte[] existing =
                            await _client.Repository.Content.GetRawContentByRef(owner, repository, input.Key,
                                checkBranch);
                        Utility.NormalizeNewlines(ref existing);

                        if (!existing.SequenceEqual(input.Value))
                            return input.Key;
                    }
                    catch (NotFoundException)
                    {
                        return input.Key;
                    }

                    return null;
                }, files);

                bool otherChanges = outdated.Any(x => x != null);

                JObject existingMeta = await TryReadMeta(owner, repository, checkBranch);
                JObject originalMeta = existingMeta != null ? (JObject)existingMeta.DeepClone() : null;
                string[] managedFiles = files.Keys
                    .Where(s => !string.Equals(s, MetaFilePath, StringComparison.Ordinal))
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToArray();

                JObject metaRoot = BuildMeta(repo.FullName, fileSet.ProfilesUsed, managedFiles, existingMeta, otherChanges, _arguments.MetaReference);
                byte[] metaBytes = Encoding.UTF8.GetBytes(metaRoot.ToString(Formatting.Indented));
                Utility.NormalizeNewlines(ref metaBytes);
                files[MetaFilePath] = metaBytes;

                bool metaOutdated = originalMeta == null || !JToken.DeepEquals(originalMeta, metaRoot);
                if (!otherChanges && !metaOutdated)
                {
                    Log.Information("{Repository}: is up-to-date", repo.FullName);
                    return;
                }

                foreach (string path in outdated.Where(s => s != null))
                    Log.Information("{Repository}: '{path}' is outdated", repo.FullName, path);

                if (metaOutdated)
                    Log.Information("{Repository}: '{path}' is outdated", repo.FullName, MetaFilePath);
            }

            NewTree newTree = new NewTree();

            IEnumerable<NewTreeItem> newTreeItems = await ParallelQueue.RunParallel(async (input, _) =>
            {
                string path = input.Key;
                byte[] value = input.Value;

                BlobReference newBlob = await _client.Git.Blob.Create(repo.Id, new NewBlob
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

            Reference headReference = await _client.Git.Reference.Get(repo.Id, $"heads/{repo.DefaultBranch}");
            string headCommit = headReference.Object.Sha;

            TreeResponse previousTree = await _client.Git.Tree.Get(repo.Id, headReference.Ref);
            newTree.BaseTree = previousTree.Sha;

            TreeResponse newTreeResponse = await _client.Git.Tree.Create(repo.Id, newTree);

            Commit createdCommit = await _client.Git.Commit.Create(repo.Id,
                new NewCommit("Updating standard content files for repository", newTreeResponse.Sha, headCommit)
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
                await _client.Git.Reference.Update(repo.Id, _branchNameRef,
                    new ReferenceUpdate(createdCommit.Sha, true));
            }
            else
            {
                Log.Information("{Repository}: Creating '{BranchName}'", repo.FullName, branchName);

                // Create
                await _client.Git.Reference.Create(repo.Id, new NewReference(_branchNameRef, createdCommit.Sha));

                // Create PR
                PullRequest newPr = await _client.Repository.PullRequest.Create(repo.Id,
                    new NewPullRequest("Auto: Updating standardized files", branchName, repo.DefaultBranch));

                string[] prLabels = _arguments.PrLabels.ToArray();
                if (prLabels is { Length: > 0 })
                {
                    Log.Debug("Adding labels {Labels} to pr {PrNumber}", _arguments.PrLabels, newPr.Number);
                    await _client.Issue.Labels.AddToIssue(repo.Id, newPr.Number, prLabels);
                }

                Log.Information("{Repository}: PR created #{PrNumber} - {Title}", repo.FullName, newPr.Number,
                    newPr.Title);
            }
        }
        catch (NotFoundException e)
        {
            throw new Exception(
                "Unable to interact with Github - perhaps the token used does not have the proper scopes? (ensure you have 'repo' and 'workflow')",
                e);
        }
    }

    private async Task<JObject> TryReadMeta(string owner, string repository, string branch)
    {
        try
        {
            byte[] existing = await _client.Repository.Content.GetRawContentByRef(owner, repository, MetaFilePath, branch);
            string json = Encoding.UTF8.GetString(existing);
            return JObject.Parse(json);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    private static JObject BuildMeta(string repoFullName, IReadOnlyList<string> profiles, IEnumerable<string> managedFiles, JObject existingMeta, bool updateTimestamp, string metaReference)
    {
        JObject root = existingMeta != null ? (JObject)existingMeta.DeepClone() : new JObject();
        JObject meta = root["meta"] as JObject ?? new JObject();

        meta["repo"] = repoFullName;
        meta["profiles"] = new JArray(profiles ?? Array.Empty<string>());
        meta["files"] = new JArray(managedFiles ?? Array.Empty<string>());

        if (updateTimestamp)
            meta["last_updated"] = DateTimeOffset.UtcNow.ToString("O");

        if (metaReference != null)
        {
            if (metaReference.Length == 0)
                meta.Remove("reference");
            else
                meta["reference"] = metaReference;
        }

        root["meta"] = meta;

        return root;
    }

    private async Task AppendLocalOverrides(string owner, string repository, string branch, Dictionary<string, byte[]> files)
    {
        foreach (string path in files.Keys.ToArray())
        {
            if (!LocalAppendFiles.Contains(path))
                continue;

            // Only repo root files
            if (path.Contains('/') || path.Contains('\\'))
                continue;

            string localPath = $"_Local/{path}";
            try
            {
                byte[] local = await _client.Repository.Content.GetRawContentByRef(owner, repository, localPath, branch);
                Utility.NormalizeNewlines(ref local);

                byte[] standard = files[path];
                Utility.NormalizeNewlines(ref standard);

                if (local.Length == 0)
                    continue;

                bool needsNewline = standard.Length > 0 && standard[^1] != (byte)'\n';
                int extra = needsNewline ? 1 : 0;
                byte[] merged = new byte[standard.Length + extra + local.Length];
                Buffer.BlockCopy(standard, 0, merged, 0, standard.Length);
                int offset = standard.Length;
                if (needsNewline)
                {
                    merged[offset] = (byte)'\n';
                    offset++;
                }
                Buffer.BlockCopy(local, 0, merged, offset, local.Length);

                files[path] = merged;
            }
            catch (NotFoundException)
            {
            }
        }
    }
}
