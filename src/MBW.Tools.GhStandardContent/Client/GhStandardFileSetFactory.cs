using System.Collections.Generic;
using System.IO;
using System.Linq;
using MBW.Tools.GhStandardContent.Model;

namespace MBW.Tools.GhStandardContent.Client
{
    class GhStandardFileSetFactory
    {
        private readonly RepositoryRoot _config;
        private readonly Dictionary<string, (string path, string file)[]> _sets;

        public GhStandardFileSetFactory(RepositoryRoot config, string configFile)
        {
            _config = config;
            _sets = new Dictionary<string, (string path, string file)[]>();

            string configDir = Path.GetDirectoryName(configFile);

            foreach ((string key, Dictionary<string, string> value) in _config.Content)
            {
                (string Key, string Value)[] setContent = value.Select(s => (s.Key, Path.GetFullPath(Path.Combine(configDir, s.Value)))).ToArray();

                _sets.Add(key, setContent);
            }
        }

        public IEnumerable<string> GetNames()
        {
            return _sets.Keys;
        }

        public GhStandardFileSet GetConfig(params string[] names)
        {
            GhStandardFileSet set = new GhStandardFileSet();

            foreach ((string path, string file) in names.SelectMany(name => _sets[name]))
            {
                using FileStream fs = File.OpenRead(file);
                set.AddFile(path, fs);
            }

            return set;
        }
    }
}