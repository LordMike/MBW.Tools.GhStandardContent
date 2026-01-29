using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MBW.Tools.GhStandardContent.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace MBW.Tools.GhStandardContent.Client;

abstract class BaseContentApplier
{
    private static readonly Dictionary<string, string> LocalOverrideMap = new(StringComparer.Ordinal)
    {
        [".gitignore"] = "_Local/.gitignore",
        [".dockerignore"] = "_Local/.dockerignore"
    };

    public async Task Apply(string repoFullName, DesiredContent desired)
    {
        // Build desired file map for this repo.
        Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(desired.Files, StringComparer.Ordinal);

        // Only fetch local override files if the base file is part of the desired set.
        IEnumerable<string> localOverrideNames = LocalOverrideMap
            .Where(kvp => files.ContainsKey(kvp.Key))
            .Select(kvp => kvp.Value);

        // Fetch current content for all desired files and meta, plus any applicable _Local overrides.
        IEnumerable<string> fetchNames = files.Keys
            .Append(MetaBuilder.MetaFilePath)
            .Concat(localOverrideNames)
            .Distinct(StringComparer.Ordinal);

        Dictionary<string, byte[]> current = await FetchFiles(repoFullName, fetchNames);

        // Merge repo-provided _Local overrides into the desired file content.
        AppendLocalOverrides(files, current);

        List<string> changed = new List<string>();
        foreach (string path in files.Keys)
        {
            byte[] desiredBytes = files[path];
            Utility.NormalizeNewlines(ref desiredBytes);
            files[path] = desiredBytes;

            if (!current.TryGetValue(path, out byte[] existing) || existing == null)
            {
                changed.Add(path);
                continue;
            }

            Utility.NormalizeNewlines(ref existing);
            if (!existing.SequenceEqual(desiredBytes))
                changed.Add(path);
        }

        // Build/update meta and treat it as a managed file for diff/apply.
        JObject existingMeta = TryParseMeta(current.TryGetValue(MetaBuilder.MetaFilePath, out byte[] metaBytes) ? metaBytes : null);
        JObject originalMeta = existingMeta != null ? (JObject)existingMeta.DeepClone() : null;

        // First build meta without touching last_updated to detect real meta drift.
        JObject metaRoot = MetaBuilder.Build(existingMeta, desired.Meta, false);
        bool metaOutdated = changed.Count > 0 || originalMeta == null || !JToken.DeepEquals(originalMeta, metaRoot);

        // If meta drifted (or other files changed), update last_updated and rebuild.
        if (metaOutdated)
            metaRoot = MetaBuilder.Build(existingMeta, desired.Meta, true);
        byte[] newMetaBytes = Encoding.UTF8.GetBytes(metaRoot.ToString(Formatting.Indented));
        Utility.NormalizeNewlines(ref newMetaBytes);
        files[MetaBuilder.MetaFilePath] = newMetaBytes;

        if (metaOutdated)
            changed.Add(MetaBuilder.MetaFilePath);

        if (changed.Count == 0)
        {
            Log.Information("{Repository}: is up-to-date", repoFullName);
            return;
        }

        foreach (string path in changed.Distinct(StringComparer.Ordinal))
            Log.Information("{Repository}: '{path}' is outdated", repoFullName, path);

        // Apply the full desired file set in a single operation.
        await ApplyFiles(repoFullName, files);
    }

    protected abstract Task<Dictionary<string, byte[]>> FetchFiles(string repoFullName, IEnumerable<string> paths);

    protected abstract Task ApplyFiles(string repoFullName, Dictionary<string, byte[]> files);

    private void AppendLocalOverrides(Dictionary<string, byte[]> files, Dictionary<string, byte[]> current)
    {
        foreach ((string path, string localPath) in LocalOverrideMap)
        {
            if (!files.TryGetValue(path, out byte[] standard))
                continue;

            byte[] local = current.TryGetValue(localPath, out byte[] val) ? val : null;
            if (local == null || local.Length == 0)
                continue;

            Utility.NormalizeNewlines(ref local);
            Utility.NormalizeNewlines(ref standard);

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
    }

    private static JObject TryParseMeta(byte[] metaBytes)
    {
        if (metaBytes == null || metaBytes.Length == 0)
            return null;

        string json = Encoding.UTF8.GetString(metaBytes);
        return JObject.Parse(json);
    }
}
