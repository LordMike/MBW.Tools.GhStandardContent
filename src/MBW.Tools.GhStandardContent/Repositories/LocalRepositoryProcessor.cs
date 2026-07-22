using MBW.Tools.GhStandardContent.Core;

namespace MBW.Tools.GhStandardContent.Repositories;

internal sealed class LocalRepositoryProcessor : IRepositoryProcessor
{
    private readonly string _repositoryPath;
    private readonly ContentPlanner _planner;

    public LocalRepositoryProcessor(string repositoryPath, ContentPlanner planner)
    {
        _repositoryPath = Path.GetFullPath(repositoryPath);
        _planner = planner;

        if (!Directory.Exists(_repositoryPath))
            throw new DirectoryNotFoundException($"Local repository '{_repositoryPath}' does not exist.");

        string gitMarker = Path.Combine(_repositoryPath, ".git");
        if (!Directory.Exists(gitMarker) && !File.Exists(gitMarker))
            throw new InvalidOperationException($"Local path '{_repositoryPath}' is not a Git worktree.");
    }

    public async Task<RepositoryResult> ProcessAsync(
        DesiredRepository desired, RunOptions options, CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> current = await ReadAsync(_planner.InitialFetchPaths(desired), cancellationToken);
        current = await ReadAsync(_planner.ExpandFetchPaths(desired, current), cancellationToken);
        DesiredRepository merged = _planner.ApplyLocalOverrides(desired, current);
        ContentPlan plan = _planner.Plan(merged, current, options.OrphanPolicy);

        if (plan.IsBlocked)
        {
            return new RepositoryResult(desired.FullName, "local", RepositoryStatus.Blocked, [], null,
                new RepositoryError("orphanPolicyRequired", plan.BlockReason ?? "An orphan policy is required."));
        }

        if (!plan.HasChanges)
        {
            RepositoryStatus status = desired.Files.Count == 0 ? RepositoryStatus.Skipped : RepositoryStatus.UpToDate;
            return new RepositoryResult(desired.FullName, "local", status, []);
        }

        if (options.Mode == RunMode.Check)
            return new RepositoryResult(desired.FullName, "local", RepositoryStatus.ChangesPending, plan.Operations);

        await ApplyAsync(plan.Operations, cancellationToken);
        return new RepositoryResult(desired.FullName, "local", RepositoryStatus.Applied, plan.Operations);
    }

    private async Task<Dictionary<string, byte[]>> ReadAsync(
        IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string path in paths.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = ManagedPath.ResolveBelowRoot(_repositoryPath, path);
            if (File.Exists(fullPath))
                files[path] = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        }

        return files;
    }

    private async Task ApplyAsync(IReadOnlyList<FileOperation> operations, CancellationToken cancellationToken)
    {
        Dictionary<FileOperation, string> staged = [];
        try
        {
            foreach (FileOperation operation in operations.Where(operation => operation.Kind != FileOperationKind.Delete))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = ManagedPath.ResolveBelowRoot(_repositoryPath, operation.Path);
                string directory = Path.GetDirectoryName(destination) ?? _repositoryPath;
                Directory.CreateDirectory(directory);
                string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.ghsc-{Guid.NewGuid():N}.tmp");
                await File.WriteAllBytesAsync(temporary, operation.Content!, cancellationToken);

                if (!OperatingSystem.IsWindows() && File.Exists(destination))
                    File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination));

                staged.Add(operation, temporary);
            }

            IEnumerable<FileOperation> writes = operations.Where(operation =>
                operation.Kind != FileOperationKind.Delete &&
                !operation.Path.Equals(ContentPlanner.MetadataPath, StringComparison.Ordinal));
            foreach (FileOperation operation in writes)
            {
                string destination = ManagedPath.ResolveBelowRoot(_repositoryPath, operation.Path);
                File.Move(staged[operation], destination, true);
                staged.Remove(operation);
            }

            foreach (FileOperation operation in operations.Where(operation =>
                         operation.Kind == FileOperationKind.Delete &&
                         !operation.Path.Equals(ContentPlanner.MetadataPath, StringComparison.Ordinal)))
            {
                string destination = ManagedPath.ResolveBelowRoot(_repositoryPath, operation.Path);
                if (File.Exists(destination))
                    File.Delete(destination);
            }

            FileOperation? metadata = operations.FirstOrDefault(operation =>
                operation.Path.Equals(ContentPlanner.MetadataPath, StringComparison.Ordinal));
            if (metadata is not null)
            {
                string destination = ManagedPath.ResolveBelowRoot(_repositoryPath, metadata.Path);
                if (metadata.Kind == FileOperationKind.Delete)
                {
                    if (File.Exists(destination))
                        File.Delete(destination);
                }
                else
                {
                    File.Move(staged[metadata], destination, true);
                    staged.Remove(metadata);
                }
            }
        }
        finally
        {
            foreach (string temporary in staged.Values)
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }
}
