namespace MBW.Tools.GhStandardContent.Core;

internal sealed record LoadedConfiguration(
    string ConfigurationPath,
    string ConfigurationDirectory,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceFile>> Profiles,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>> Repositories);