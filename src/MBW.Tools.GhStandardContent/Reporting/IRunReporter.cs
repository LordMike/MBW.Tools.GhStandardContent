using MBW.Tools.GhStandardContent.Core;

namespace MBW.Tools.GhStandardContent.Reporting;

internal interface IRunReporter
{
    void RepositoryStarted(string repository);
    void Write(RunSummary summary);
    void WriteCancellation();
}