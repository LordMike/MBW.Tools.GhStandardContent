using System;
using System.Collections.Generic;
using System.Linq;

namespace MBW.Tools.GhStandardContent.Client;

class DesiredContentBuilder
{
    private const string MetaFilePath = ".standard_content.json";

    public DesiredContent Build(string repoFullName, GhStandardFileSet fileSet, string metaReference)
    {
        Dictionary<string, byte[]> files = fileSet
            .GetFiles()
            .ToDictionary(s => s.path, s => s.value, StringComparer.Ordinal);

        string[] managedFiles = files.Keys
            .Where(s => !string.Equals(s, MetaFilePath, StringComparison.Ordinal))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        MetaInfo meta = new MetaInfo
        {
            RepoFullName = repoFullName,
            Profiles = fileSet.ProfilesUsed ?? Array.Empty<string>(),
            ManagedFiles = managedFiles,
            MetaReference = metaReference
        };

        return new DesiredContent(files, meta);
    }
}
