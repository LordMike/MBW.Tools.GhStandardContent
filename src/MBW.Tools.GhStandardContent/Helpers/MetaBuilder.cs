using System;
using System.Linq;
using MBW.Tools.GhStandardContent.Client;
using Newtonsoft.Json.Linq;

namespace MBW.Tools.GhStandardContent.Helpers;

static class MetaBuilder
{
    public const string MetaFilePath = ".standard_content.json";

    public static JObject Build(JObject existingMeta, MetaInfo metaInfo, bool updateTimestamp)
    {
        JObject root = existingMeta != null ? (JObject)existingMeta.DeepClone() : new JObject();
        JObject meta = root["meta"] as JObject ?? new JObject();

        root["$schema"] = "https://github.com/LordMike/MBW.Tools.GhStandardContent/spec/StandardContent.json";
        meta["repo"] = metaInfo.RepoFullName;
        meta["profiles"] = new JArray(metaInfo.Profiles ?? Array.Empty<string>());
        meta["files"] = new JArray(metaInfo.ManagedFiles ?? Array.Empty<string>());

        if (updateTimestamp)
            meta["last_updated"] = DateTimeOffset.UtcNow.ToString("O");

        if (metaInfo.MetaReference != null)
        {
            if (metaInfo.MetaReference.Length == 0)
                meta.Remove("reference");
            else
                meta["reference"] = metaInfo.MetaReference;
        }

        root["meta"] = meta;
        return root;
    }
}
