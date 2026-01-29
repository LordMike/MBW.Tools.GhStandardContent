using System;
using System.Collections.Generic;
using System.IO;
using MBW.Tools.GhStandardContent.Helpers;

namespace MBW.Tools.GhStandardContent.Client;

class GhStandardFileSet
{
    private readonly Dictionary<string, byte[]> _desiredContent;

    public IReadOnlyList<string> ProfilesUsed { get; set; } = Array.Empty<string>();

    public int Count => _desiredContent.Count;

    public GhStandardFileSet()
    {
        _desiredContent = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    }

    public void AddFile(string path, Stream content)
    {
        using MemoryStream ms = new MemoryStream();
        content.CopyTo(ms);

        var data = ms.ToArray();
        Utility.NormalizeNewlines(ref data);

        _desiredContent.Add(path, data);
    }

    public IEnumerable<(string path, byte[] value)> GetFiles()
    {
        foreach ((string key, byte[] value) in _desiredContent)
        {
            yield return (key, value);
        }
    }
}
