namespace MBW.Tools.GhStandardContent.Core;

internal sealed record DesiredRepository(
    string FullName,
    IReadOnlyList<string> Profiles,
    IReadOnlyDictionary<string, byte[]> Files,
    string? MetaReference);