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

        if (!File.Exists(parsedArgs.RepositoryJson))
        {
            Log.Logger.Error($"Unable to locate {parsedArgs.RepositoryJson}");
            return 2;
        }

        RepositoryRoot configRoot = JsonConvert.DeserializeObject<RepositoryRoot>(await File.ReadAllTextAsync(parsedArgs.RepositoryJson));

        IHost host = new HostBuilder()
            .ConfigureLogging(loggingBuilder =>
            {
                loggingBuilder.AddSerilog(Log.Logger);
            })
            .ConfigureServices(services =>
            {
                if (!string.IsNullOrEmpty(parsedArgs.ProxyUrl))
                    services.AddSingleton<IWebProxy>(_ => new WebProxy(parsedArgs.ProxyUrl));

                services.AddSingleton<IGitHubClient>(provider =>
                {
                    IWebProxy proxy = provider.GetService<IWebProxy>();
                    SimpleJsonSerializer jsonSerializer = new SimpleJsonSerializer();

                    return new GitHubClient(new Connection(new ProductHeaderValue(ClientName), new Uri(parsedArgs.GithubApi),
                        new InMemoryCredentialStore(new Credentials(parsedArgs.GithubToken)), new HttpClientAdapter(
                            () => new HttpClientHandler
                            {
                                Proxy = proxy
                            }), jsonSerializer));
                });

                services
                    .AddSingleton(parsedArgs)
                    .AddSingleton(configRoot)
                    .AddSingleton(x => new GhStandardFileSetFactory(x.GetRequiredService<RepositoryRoot>(), parsedArgs.RepositoryJson))
                    .AddSingleton<GhStandardContentApplier>();
            })
            .Build();

        GhStandardFileSetFactory fileSetFactory = host.Services.GetRequiredService<GhStandardFileSetFactory>();
        GhStandardContentApplier applier = host.Services.GetRequiredService<GhStandardContentApplier>();

        IEnumerable<KeyValuePair<string, JObject>> repos;

        if (!string.IsNullOrEmpty(parsedArgs.Repository) &&
            configRoot.Repositories.TryGetValue(parsedArgs.Repository, out var singleRepo))
        {
            repos = new[] { KeyValuePair.Create(parsedArgs.Repository, singleRepo) };
        }
        else if (!string.IsNullOrEmpty(parsedArgs.Repository))
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
        else
            repos = configRoot.Repositories;

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

            Log.Information("Applying {Count} files to {Organization} / {Repository}", fileSet.Count, repoOrg, repoName);

            await applier.Apply(repoOrg, repoName, fileSet);
        }

        return 0;
    }
}