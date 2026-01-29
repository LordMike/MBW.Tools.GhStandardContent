using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MBW.Tools.GhStandardContent.Helpers;

namespace MBW.Tools.GhStandardContent.Client;

class LocalContentApplier : BaseContentApplier
{
    private readonly string _repoPath;

    public LocalContentApplier(string repoPath)
    {
        _repoPath = repoPath;
    }

    protected override Task<Dictionary<string, byte[]>> FetchFiles(string repoFullName, IEnumerable<string> paths)
    {
        Dictionary<string, byte[]> results = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (string path in paths)
        {
            string fullPath = Path.Combine(_repoPath, path);
            if (!File.Exists(fullPath))
            {
                results[path] = null;
                continue;
            }

            byte[] existing = File.ReadAllBytes(fullPath);
            Utility.NormalizeNewlines(ref existing);
            results[path] = existing;
        }

        return Task.FromResult(results);
    }

    protected override Task ApplyFiles(string repoFullName, Dictionary<string, byte[]> files)
    {
        foreach ((string path, byte[] desiredBytes) in files)
        {
            string fullPath = Path.Combine(_repoPath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? _repoPath);
            File.WriteAllBytes(fullPath, desiredBytes);
        }

        return Task.CompletedTask;
    }

    // Local overrides are fetched via FetchFiles when requested.
}
