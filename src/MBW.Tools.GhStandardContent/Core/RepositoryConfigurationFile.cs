using System.Text.Json.Serialization;

namespace MBW.Tools.GhStandardContent.Core;

internal sealed class RepositoryConfigurationFile
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("content")]
    public Dictionary<string, Dictionary<string, string>>? Content { get; set; }

    [JsonPropertyName("repositories")]
    public Dictionary<string, System.Text.Json.JsonElement>? Repositories { get; set; }
}
