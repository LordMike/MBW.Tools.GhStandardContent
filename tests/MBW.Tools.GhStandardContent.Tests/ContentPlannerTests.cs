using System.Text;
using MBW.Tools.GhStandardContent.Core;

namespace MBW.Tools.GhStandardContent.Tests;

public sealed class ContentPlannerTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 22, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Plan_IsStableAfterApplyingItsOperations()
    {
        ContentPlanner planner = new(() => FixedTime);
        DesiredRepository desired = Desired(new Dictionary<string, byte[]>
        {
            ["README.md"] = "hello\n"u8.ToArray()
        });

        ContentPlan first = planner.Plan(desired, new Dictionary<string, byte[]>(), OrphanPolicy.Error);
        Assert.Collection(first.Operations.OrderBy(operation => operation.Path),
            operation => Assert.Equal(ContentPlanner.MetadataPath, operation.Path),
            operation => Assert.Equal("README.md", operation.Path));

        Dictionary<string, byte[]> applied = Apply(new Dictionary<string, byte[]>(), first.Operations);
        ContentPlan second = planner.Plan(desired, applied, OrphanPolicy.Error);
        Assert.Empty(second.Operations);
    }

    [Fact]
    public void Plan_RequiresExplicitOrphanPolicy()
    {
        ContentPlanner planner = new(() => FixedTime);
        Dictionary<string, byte[]> current = ExistingManaged("obsolete.txt");

        ContentPlan plan = planner.Plan(Desired(new Dictionary<string, byte[]>()), current, OrphanPolicy.Error);

        Assert.True(plan.IsBlocked);
        Assert.Equal(["obsolete.txt"], plan.OrphanedFiles);
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void Plan_KeepFinalOrphanRemovesOnlyMetadata()
    {
        ContentPlanner planner = new(() => FixedTime);
        Dictionary<string, byte[]> current = ExistingManaged("obsolete.txt");

        ContentPlan plan = planner.Plan(Desired(new Dictionary<string, byte[]>()), current, OrphanPolicy.Keep);

        FileOperation operation = Assert.Single(plan.Operations);
        Assert.Equal(ContentPlanner.MetadataPath, operation.Path);
        Assert.Equal(FileOperationKind.Delete, operation.Kind);
    }

    [Fact]
    public void Plan_DeleteFinalOrphanRemovesFileThenMetadata()
    {
        ContentPlanner planner = new(() => FixedTime);
        Dictionary<string, byte[]> current = ExistingManaged("obsolete.txt");

        ContentPlan plan = planner.Plan(Desired(new Dictionary<string, byte[]>()), current, OrphanPolicy.Delete);

        Assert.Equal(2, plan.Operations.Count);
        Assert.Contains(plan.Operations, operation => operation.Path == "obsolete.txt" && operation.Kind == FileOperationKind.Delete);
        Assert.Equal(ContentPlanner.MetadataPath, plan.Operations[^1].Path);
    }

    [Fact]
    public void LocalOverride_IsAppendedAsNormalizedText()
    {
        ContentPlanner planner = new();
        DesiredRepository desired = Desired(new Dictionary<string, byte[]> { [".gitignore"] = "bin\r\n"u8.ToArray() });
        Dictionary<string, byte[]> current = new(StringComparer.Ordinal)
        {
            ["_Local/.gitignore"] = "local\r\n"u8.ToArray()
        };

        DesiredRepository merged = planner.ApplyLocalOverrides(desired, current);

        Assert.Equal("bin\nlocal\n", Encoding.UTF8.GetString(merged.Files[".gitignore"]));
    }

    [Fact]
    public void BinaryContent_IsNotNewlineNormalized()
    {
        byte[] binary = [0xff, 0x0d, 0x0a, 0x00];
        Assert.Same(binary, TextContent.Normalize(binary));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/drive")]
    [InlineData(".git/config")]
    public void ManagedPath_RejectsUnsafePaths(string path)
    {
        Assert.False(ManagedPath.TryNormalize(path, out _, out _));
    }

    private static DesiredRepository Desired(IReadOnlyDictionary<string, byte[]> files) =>
        new("owner/repo", ["standard"], files, null);

    private static Dictionary<string, byte[]> ExistingManaged(string path) => new(StringComparer.Ordinal)
    {
        [path] = "old"u8.ToArray(),
        [ContentPlanner.MetadataPath] = Encoding.UTF8.GetBytes($$"""
            {
              "meta": {
                "repo": "owner/repo",
                "profiles": ["old"],
                "files": ["{{path}}"]
              }
            }
            """)
    };

    private static Dictionary<string, byte[]> Apply(
        Dictionary<string, byte[]> current, IEnumerable<FileOperation> operations)
    {
        Dictionary<string, byte[]> result = new(current, StringComparer.Ordinal);
        foreach (FileOperation operation in operations)
        {
            if (operation.Kind == FileOperationKind.Delete)
                result.Remove(operation.Path);
            else
                result[operation.Path] = operation.Content!;
        }

        return result;
    }
}
