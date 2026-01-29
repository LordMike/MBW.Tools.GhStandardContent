using System;
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

        GhStandardFileSet set = fileSetFactory.GetConfig(namesToApply);
        set.ProfilesUsed = namesToApply.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        return set;
    }
}
