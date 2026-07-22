namespace MBW.Tools.GhStandardContent.Core;

internal sealed record ContentPlan(
    IReadOnlyList<FileOperation> Operations,
    IReadOnlyList<string> OrphanedFiles,
    bool IsBlocked,
    string? BlockReason)
{
    public bool HasChanges => Operations.Count > 0;
}