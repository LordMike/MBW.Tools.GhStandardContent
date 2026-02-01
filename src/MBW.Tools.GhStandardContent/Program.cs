using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using CommandLine;
using MBW.Tools.GhStandardContent.Client;
using MBW.Tools.GhStandardContent.Helpers;
using MBW.Tools.GhStandardContent.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Octokit;
using Octokit.Internal;
using Serilog;

namespace MBW.Tools.GhStandardContent;

class Program
{
    private const string ClientName = "mbwarez-sc-client";

    static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        ParserResult<CommandlineArgs> argsResult = Parser.Default.ParseArguments<CommandlineArgs>(args);
        if (argsResult is NotParsed<CommandlineArgs>)
        {
            Log.Logger.Error("Unable to parse arguments, be sure to include the path to the 'repos.json' file");

            return 1;
        }

        CommandlineArgs parsedArgs = ((Parsed<CommandlineArgs>)argsResult).Value;

        bool isLocal = !string.IsNullOrEmpty(parsedArgs.LocalPath);
        if (!isLocal && string.IsNullOrEmpty(parsedArgs.GithubToken))
        {
            Log.Error("Missing --gh-token for GitHub mode");
            return 5;
        }

        if (!File.Exists(parsedArgs.RepositoryJson))
        {
            Log.Logger.Error($"Unable to locate {parsedArgs.RepositoryJson}");
            return 2;
        }

        RepositoryRoot configRoot =
            JsonConvert.DeserializeObject<RepositoryRoot>(await File.ReadAllTextAsync(parsedArgs.RepositoryJson));

        IHost host = new HostBuilder()
            .ConfigureLogging(loggingBuilder => { loggingBuilder.AddSerilog(Log.Logger); })
            .ConfigureServices(services =>
            {
                if (!string.IsNullOrEmpty(parsedArgs.ProxyUrl))
                    services.AddSingleton<IWebProxy>(_ => new WebProxy(parsedArgs.ProxyUrl));

                if (!isLocal)
                {
                    services.AddSingleton<IGitHubClient>(provider =>
                    {
                        IWebProxy proxy = provider.GetService<IWebProxy>();
                        SimpleJsonSerializer jsonSerializer = new SimpleJsonSerializer();

                        return new GitHubClient(new Connection(new ProductHeaderValue(ClientName),
                            new Uri(parsedArgs.GithubApi),
                            new InMemoryCredentialStore(new Credentials(parsedArgs.GithubToken)),
                            new HttpClientAdapter(() => new HttpClientHandler
                            {
                                Proxy = proxy
                            }), jsonSerializer));
                    });
                }

                services
                    .AddSingleton(parsedArgs)
                    .AddSingleton(configRoot)
                    .AddSingleton(x =>
                        new GhStandardFileSetFactory(x.GetRequiredService<RepositoryRoot>(),
                            parsedArgs.RepositoryJson));

                if (!isLocal)
                    services.AddTransient<GhStandardContentApplier>();
            })
            .Build();

        GhStandardFileSetFactory fileSetFactory = host.Services.GetRequiredService<GhStandardFileSetFactory>();
        DesiredContentBuilder desiredBuilder = new DesiredContentBuilder();

        IEnumerable<KeyValuePair<string, JObject>> repos;

        if (!isLocal && !string.IsNullOrEmpty(parsedArgs.Repository) &&
            configRoot.Repositories.TryGetValue(parsedArgs.Repository, out var singleRepo))
        {
            repos = new[] { KeyValuePair.Create(parsedArgs.Repository, singleRepo) };
        }
        else if (!isLocal && !string.IsNullOrEmpty(parsedArgs.Repository))
        {
            // Try looking for a partial match
            var match = configRoot.Repositories.Keys.FirstOrDefault(s =>
                s.Split('/').Last().Equals(parsedArgs.Repository, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                Log.Error("Unable to run for {Repository} alone", parsedArgs.Repository);
                return 3;
            }

            repos = new[] { KeyValuePair.Create(match, configRoot.Repositories[match]) };
        }
        else if (!isLocal)
            repos = configRoot.Repositories;
        else
        {
            string localRepoName;
            try
            {
                localRepoName = ResolveLocalRepoName(parsedArgs, configRoot);
            }
            catch (Exception e)
            {
                Log.Error(e, "Unable to resolve local repo name");
                return 4;
            }

            if (!configRoot.Repositories.TryGetValue(localRepoName, out JObject localConfig))
            {
                Log.Error("Unable to find local repo {Repository} in repos.json", localRepoName);
                return 4;
            }

            repos = new[] { KeyValuePair.Create(localRepoName, localConfig) };
        }

        foreach ((string repository, JObject config) in repos)
        {
            GhStandardFileSet fileSet = fileSetFactory.GetFileSet(config);
            if (fileSet.Count == 0)
            {
                Log.Debug("Skipping {Repository}, no files to manage", repository);
                continue;
            }

            string[] repoParts = repository.Split('/');

            string repoOrg = repoParts[0];
            string repoName = repoParts[1];

            DesiredContent desired = desiredBuilder.Build(repository, fileSet, parsedArgs.MetaReference);

            if (isLocal)
            {
                LocalContentApplier localApplier = new LocalContentApplier(parsedArgs.LocalPath);
                await localApplier.Apply(repository, desired, parsedArgs.RemovalMode);
            }
            else
            {
                GhStandardContentApplier applier = host.Services.GetRequiredService<GhStandardContentApplier>();
                Log.Information("Applying {Count} files to {Organization} / {Repository}", fileSet.Count, repoOrg, repoName);
                await applier.Apply(repository, desired, parsedArgs.RemovalMode);
            }
        }

        return 0;
    }

    private static string ResolveLocalRepoName(CommandlineArgs args, RepositoryRoot configRoot)
    {
        if (!string.IsNullOrEmpty(args.Repository))
        {
            if (args.Repository.Contains('/'))
                return args.Repository;

            string match = configRoot.Repositories.Keys.FirstOrDefault(s =>
                s.Split('/').Last().Equals(args.Repository, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                throw new Exception($"Repo '{args.Repository}' not found in repos.json");

            return match;
        }

        string folderName = new DirectoryInfo(args.LocalPath).Name;
        string[] matches = configRoot.Repositories.Keys
            .Where(s => s.Split('/').Last().Equals(folderName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
            throw new Exception($"Repo for folder '{folderName}' not found in repos.json");
        if (matches.Length > 1)
            throw new Exception($"Folder name '{folderName}' matches multiple repos in repos.json");

        return matches[0];
    }
}