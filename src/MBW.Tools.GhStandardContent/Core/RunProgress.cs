namespace MBW.Tools.GhStandardContent.Core;

internal enum RunProgressPhase
{
    Starting,
    Processing,
    Waiting,
    Finalizing
}

internal sealed record RunProgress(
    int Total,
    int Completed,
    int Running,
    int Queued,
    IReadOnlyList<string> ActiveRepositories,
    RunProgressPhase Phase,
    string? StatusRepository);
