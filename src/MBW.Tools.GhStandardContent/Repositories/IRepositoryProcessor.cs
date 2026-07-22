using MBW.Tools.GhStandardContent.Core;

namespace MBW.Tools.GhStandardContent.Repositories;

internal interface IRepositoryProcessor
{
    Task<RepositoryResult> ProcessAsync(DesiredRepository desired, RunOptions options, CancellationToken cancellationToken);
}
