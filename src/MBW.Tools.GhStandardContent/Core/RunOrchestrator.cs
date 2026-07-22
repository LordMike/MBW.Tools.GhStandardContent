using System.Collections.Concurrent;
using MBW.Tools.GhStandardContent.Configuration;
using MBW.Tools.GhStandardContent.Repositories;
using Octokit;

namespace MBW.Tools.GhStandardContent.Core;

internal sealed class RunOrchestrator
{
    private readonly ConfigurationLoader _configurationLoader;
    private readonly ContentPlanner _planner;

    public RunOrchestrator(ConfigurationLoader configurationLoader, ContentPlanner planner)
    {
        _configurationLoader = configurationLoader;
        _planner = planner;
    }

    public async Task<RunSummary> RunAsync(
        RunOptions options, Action<string>? repositoryStarted, CancellationToken cancellationToken)
    {
        (LoadedConfiguration? configuration, IReadOnlyList<ValidationDiagnostic> diagnostics) =
            await _configurationLoader.LoadAsync(options.ConfigurationPath, cancellationToken);

        if (configuration is null || diagnostics.Count > 0)
            return new RunSummary(options.Mode, "invalid", [], diagnostics);

        IReadOnlyList<string> repositories;
        try
        {
            repositories = ResolveRepositories(configuration, options);
        }
        catch (Exception exception)
        {
            return new RunSummary(options.Mode, "invalid", [],
                [new ValidationDiagnostic("repository.selection", exception.Message)]);
        }

        if (options.Mode == RunMode.Validate)
            return new RunSummary(options.Mode, "valid", [], []);

        IRepositoryProcessor processor;
        IDisposable? disposable = null;
        try
        {
            if (options.LocalPath is not null)
                processor = new LocalRepositoryProcessor(options.LocalPath, _planner);
            else
            {
                string? token = ResolveToken(options.GitHubApi);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return new RunSummary(options.Mode, "invalid", [],
                        [new ValidationDiagnostic("github.tokenMissing",
                            "GitHub mode requires GH_TOKEN or GITHUB_TOKEN (enterprise variants are also supported).")]);
                }

                GitHubRepositoryProcessor github = new(options.GitHubApi, options.Proxy, token, _planner);
                processor = github;
                disposable = github;
            }
        }
        catch (Exception exception)
        {
            return new RunSummary(options.Mode, "invalid", [],
                [new ValidationDiagnostic("target.invalid", exception.Message)]);
        }

        using (disposable)
        {
            ConcurrentBag<RepositoryResult> results = [];
            await Parallel.ForEachAsync(repositories,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.LocalPath is null ? options.Parallelism : 1,
                    CancellationToken = cancellationToken
                },
                async (repository, token) =>
                {
                    repositoryStarted?.Invoke(repository);
                    try
                    {
                        DesiredRepository desired = await _configurationLoader.BuildDesiredAsync(
                            configuration, repository, options.MetaReference, token);
                        results.Add(await processor.ProcessAsync(desired, options, token));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        results.Add(RepositoryResult.Failed(repository,
                            options.LocalPath is null ? "github" : "local",
                            ErrorCode(exception), exception.Message,
                            options.Verbosity == OutputVerbosity.Detailed ? exception.ToString() : null));
                    }
                });

            RepositoryResult[] ordered = results.OrderBy(result => result.Repository, StringComparer.OrdinalIgnoreCase).ToArray();
            string result = ordered.Any(item => item.Status is RepositoryStatus.Failed or RepositoryStatus.Blocked)
                ? "partialFailure"
                : ordered.Any(item => item.Status is RepositoryStatus.ChangesPending or RepositoryStatus.PullRequestOpen)
                    ? "changesPending"
                    : "success";
            return new RunSummary(options.Mode, result, ordered, []);
        }
    }

    private static IReadOnlyList<string> ResolveRepositories(LoadedConfiguration configuration, RunOptions options)
    {
        if (options.LocalPath is not null)
        {
            if (options.Repositories.Count > 1)
                throw new InvalidOperationException("Local mode accepts at most one --repository value.");

            string? selector = options.Repositories.SingleOrDefault() ?? TryReadOriginRepository(options.LocalPath);
            selector ??= new DirectoryInfo(Path.GetFullPath(options.LocalPath)).Name;
            return [ResolveRepository(configuration, selector)];
        }

        if (options.Repositories.Count == 0)
            return configuration.Repositories.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

        return options.Repositories.Select(selector => ResolveRepository(configuration, selector))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveRepository(LoadedConfiguration configuration, string selector)
    {
        if (configuration.Repositories.ContainsKey(selector))
            return configuration.Repositories.Keys.First(name => name.Equals(selector, StringComparison.OrdinalIgnoreCase));

        string[] matches = configuration.Repositories.Keys
            .Where(name => name.Split('/')[1].Equals(selector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Repository selector '{selector}' does not match the configuration."),
            _ => throw new InvalidOperationException(
                $"Repository selector '{selector}' is ambiguous: {string.Join(", ", matches.OrderBy(value => value))}.")
        };
    }

    private static string? TryReadOriginRepository(string repositoryPath)
    {
        string marker = Path.Combine(Path.GetFullPath(repositoryPath), ".git");
        string? configPath = Directory.Exists(marker) ? Path.Combine(marker, "config") : ResolveWorktreeConfig(marker);
        if (configPath is null || !File.Exists(configPath))
            return null;

        bool inOrigin = false;
        foreach (string rawLine in File.ReadLines(configPath))
        {
            string line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                inOrigin = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inOrigin || !line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
                continue;

            int separator = line.IndexOf('=');
            if (separator >= 0)
                return ParseRepositoryUrl(line[(separator + 1)..].Trim());
        }

        return null;
    }

    private static string? ResolveWorktreeConfig(string marker)
    {
        if (!File.Exists(marker))
            return null;
        string line = File.ReadLines(marker).FirstOrDefault() ?? string.Empty;
        if (!line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            return null;
        string gitDirectory = line[7..].Trim();
        if (!Path.IsPathRooted(gitDirectory))
            gitDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(marker)!, gitDirectory));
        return Path.Combine(gitDirectory, "config");
    }

    private static string? ParseRepositoryUrl(string url)
    {
        string path;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            path = uri.AbsolutePath;
        else
        {
            int colon = url.IndexOf(':');
            path = colon >= 0 ? url[(colon + 1)..] : url;
        }

        string[] parts = path.Trim('/').Split('/');
        if (parts.Length < 2)
            return null;
        string repository = parts[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[^1][..^4]
            : parts[^1];
        return $"{parts[^2]}/{repository}";
    }

    private static string? ResolveToken(Uri api)
    {
        bool enterprise = !api.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);
        if (enterprise)
        {
            string? enterpriseToken = Environment.GetEnvironmentVariable("GH_ENTERPRISE_TOKEN") ??
                                      Environment.GetEnvironmentVariable("GITHUB_ENTERPRISE_TOKEN");
            if (!string.IsNullOrWhiteSpace(enterpriseToken))
                return enterpriseToken;
        }

        return Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    }

    private static string ErrorCode(Exception exception) => exception switch
    {
        RateLimitExceededException => "github.rateLimit",
        AuthorizationException => "github.unauthorized",
        NotFoundException => "github.notFound",
        ApiException apiException when apiException.StatusCode is System.Net.HttpStatusCode.Forbidden or
            System.Net.HttpStatusCode.Unauthorized => "github.permissionDenied",
        ApiException => "github.api",
        HttpRequestException => "github.http",
        FileNotFoundException => "file.notFound",
        DirectoryNotFoundException => "directory.notFound",
        UnauthorizedAccessException => "access.denied",
        InvalidOperationException => "operation.invalid",
        _ => "unexpected"
    };
}
