namespace MBW.Tools.GhStandardContent.Core;

internal enum RepositoryReason
{
    ContentMismatch,
    UnexpectedChanges,
    BehindBase,
    LabelsMissing,
    HeadChanged,
    NoResults,
    Expected,
    Pending,
    Failure,
    Error,
    Draft,
    Conflicts,
    ReviewRequired,
    ChangesRequested,
    PolicyBlocked,
    MergeabilityUnknown,
    SquashUnavailable,
    MergeRejected
}
