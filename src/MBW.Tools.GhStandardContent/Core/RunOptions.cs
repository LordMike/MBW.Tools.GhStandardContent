namespace MBW.Tools.GhStandardContent.Core;

internal sealed record RunOptions(
    RunMode Mode,
    string ConfigurationPath,
    OutputFormat Format,
    ColorMode Color,
    OutputVerbosity Verbosity,
    IReadOnlyList<string> Repositories,
    string? LocalPath,
    Uri GitHubApi,
    Uri? Proxy,
    int Parallelism,
    string BranchName,
    string CommitAuthor,
    string CommitEmail,
    IReadOnlyList<string> Labels,
    string? MetaReference,
    OrphanPolicy OrphanPolicy);