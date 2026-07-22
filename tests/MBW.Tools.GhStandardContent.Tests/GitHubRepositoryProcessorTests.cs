using System.Text.Json.Nodes;
using MBW.Tools.GhStandardContent.Core;
using MBW.Tools.GhStandardContent.Repositories;

namespace MBW.Tools.GhStandardContent.Tests;

public sealed class GitHubRepositoryProcessorTests
{
    [Fact]
    public void CreateTreeBodyUsesExplicitJsonNullForDeletion()
    {
        FileOperation[] operations =
        [
            new("updated.txt", FileOperationKind.Update, "new"u8.ToArray()),
            new("deleted.txt", FileOperationKind.Delete)
        ];
        Dictionary<string, string> blobs = new(StringComparer.Ordinal) { ["updated.txt"] = "blob-sha" };

        JsonObject body = GitHubRepositoryProcessor.BuildCreateTreeBody("base-sha", operations, blobs);

        Assert.Equal("base-sha", body["base_tree"]!.GetValue<string>());
        JsonArray tree = body["tree"]!.AsArray();
        Assert.Equal("blob-sha", tree[0]!["sha"]!.GetValue<string>());
        Assert.Contains("\"sha\":null", body.ToJsonString(), StringComparison.Ordinal);
    }
}
