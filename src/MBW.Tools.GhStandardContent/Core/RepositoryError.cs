namespace MBW.Tools.GhStandardContent.Core;

internal sealed record RepositoryError(string Code, string Message, string? Detail = null);