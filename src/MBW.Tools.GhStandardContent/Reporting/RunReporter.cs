using MBW.Tools.GhStandardContent.Core;

namespace MBW.Tools.GhStandardContent.Reporting;

internal static class RunReporter
{
    public static IRunReporter Create(RunOptions options) => options.Format == OutputFormat.Json
        ? new JsonRunReporter()
        : new TextRunReporter(options.Color, options.Verbosity);
}