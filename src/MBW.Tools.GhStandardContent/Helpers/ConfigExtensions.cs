using System.Linq;
using MBW.Tools.GhStandardContent.Client;
using Newtonsoft.Json.Linq;

namespace MBW.Tools.GhStandardContent.Helpers;

static class ConfigExtensions
{
    public static GhStandardFileSet GetFileSet(this GhStandardFileSetFactory fileSetFactory, JObject repositoryConfig)
    {
        string[] namesToApply = fileSetFactory
            .GetNames()
            .Where(name => repositoryConfig.TryGetValue(name, out JToken token) && token.Value<bool>())
            .ToArray();

        return fileSetFactory.GetConfig(namesToApply);
    }
}