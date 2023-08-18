using CommandLine;

namespace MBW.Tools.GhStandardContent;

class CommandlineArgs
{
    [Option('t', "gh-token", Required = true)]
    public string GithubToken { get; set; }

    [Option("gh-api", Hidden = true, Default = "https://api.github.com/")]
    public string GithubApi { get; set; }

    [Option("branch-name", Default = "feature/auto-contents")]
    public string BranchName { get; set; }

    [Option("commit-author", Default = "Auto Contents")]
    public string CommitAuthor { get; set; }

    [Option("commit-email", Default = "AutoContents@example.org")]
    public string CommitEmail { get; set; }

    [Option("proxy")]
    public string ProxyUrl { get; set; }

    [Option('r', "repo")]
    public string Repository { get; set; }

    [Value(0, Required = true)]
    public string RepositoryJson { get; set; }
}