namespace MBW.Tools.GhStandardContent.Core;

internal sealed record PullRequestInfo(int Number, string Url, bool Created, int BehindBy = 0);
