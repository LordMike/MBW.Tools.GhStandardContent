using System.Collections.Generic;

namespace MBW.Tools.GhStandardContent.Client;

class DesiredContent
{
    public DesiredContent(Dictionary<string, byte[]> files, MetaInfo meta)
    {
        Files = files;
        Meta = meta;
    }

    public Dictionary<string, byte[]> Files { get; }

    public MetaInfo Meta { get; }
}
