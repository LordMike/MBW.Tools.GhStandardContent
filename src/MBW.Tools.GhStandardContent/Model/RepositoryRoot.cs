using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace MBW.Tools.GhStandardContent.Model
{
    class RepositoryRoot
    {
        public Dictionary<string, Dictionary<string, string>> Content { get; set; }

        public Dictionary<string, JObject> Repositories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}