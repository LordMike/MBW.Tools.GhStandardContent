using MBW.Tools.GhStandardContent.Core;

namespace MBW.Tools.GhStandardContent.Reporting;

internal interface IRunReporter
{
    Task<RunSummary> RunWithProgressAsync(Func<Action<RunProgress>?, Task<RunSummary>> operation);
    void Write(RunSummary summary);
    void WriteCancellation();
}
