using System;
using System.Linq;
using JetBrains.Annotations;
using MBW.Tools.GhStandardContent.Client;
using Newtonsoft.Json.Linq;

namespace MBW.Tools.GhStandardContent.Helpers;

static class MetaBuilder
{
    public const string MetaFilePath = ".standard_content.json";

    public static JObject Build([CanBeNull] JObject metaObject, MetaInfo metaInfo)
    {
        JObject root = metaObject ?? new JObject();
        JObject meta = root["meta"] as JObject ?? new JObject();

        root["$schema"] = "https://github.com/LordMike/MBW.Tools.GhStandardContent/spec/StandardContent.json";
        meta["repo"] = metaInfo.RepoFullName;
        meta["profiles"] = new JArray(metaInfo.Profiles ?? Array.Empty<string>());
        meta["files"] = new JArray(metaInfo.ManagedFiles ?? Array.Empty<string>());

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

    public static void SetTimestamp(JObject metaObject)
    {
        JObject meta = (JObject)metaObject["meta"];
        meta["last_updated"] = DateTimeOffset.UtcNow.ToString("O");
    }
}