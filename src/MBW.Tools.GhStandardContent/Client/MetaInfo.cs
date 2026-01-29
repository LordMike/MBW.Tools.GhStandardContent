using System.Collections.Generic;

namespace MBW.Tools.GhStandardContent.Client;

class MetaInfo
{
    public string RepoFullName { get; init; }
    public IReadOnlyList<string> Profiles { get; init; }
    public IReadOnlyList<string> ManagedFiles { get; init; }
    public string MetaReference { get; init; }
}
