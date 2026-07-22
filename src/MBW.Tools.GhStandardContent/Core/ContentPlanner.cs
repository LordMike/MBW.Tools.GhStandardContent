using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MBW.Tools.GhStandardContent.Core;

internal sealed class ContentPlanner
{
    public const string MetadataPath = ".standard_content.json";
    public const string MetadataSchema =
        "https://raw.githubusercontent.com/LordMike/MBW.Tools.GhStandardContent/master/spec/StandardContent.json";

    private static readonly IReadOnlyDictionary<string, string> OverridePaths =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".gitignore"] = "_Local/.gitignore",
            [".gitattributes"] = "_Local/.gitattributes",
            [".dockerignore"] = "_Local/.dockerignore",
            [".editorconfig"] = "_Local/.editorconfig"
        };

    private static readonly JsonSerializerOptions MetaJsonOptions = new() { WriteIndented = true };
    private readonly Func<DateTimeOffset> _clock;

    public ContentPlanner(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyCollection<string> InitialFetchPaths(DesiredRepository desired) =>
        desired.Files.Keys
            .Append(MetadataPath)
            .Concat(OverridePaths.Where(pair => desired.Files.ContainsKey(pair.Key)).Select(pair => pair.Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyCollection<string> ExpandFetchPaths(
        DesiredRepository desired, IReadOnlyDictionary<string, byte[]> current)
    {
        IEnumerable<string> previousManaged = TryGetManagedFiles(current.GetValueOrDefault(MetadataPath));
        return InitialFetchPaths(desired)
            .Concat(previousManaged)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public DesiredRepository ApplyLocalOverrides(
        DesiredRepository desired, IReadOnlyDictionary<string, byte[]> current)
    {
        Dictionary<string, byte[]> files = desired.Files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach ((string standardPath, string overridePath) in OverridePaths)
        {
            if (!files.TryGetValue(standardPath, out byte[]? standard) ||
                !current.TryGetValue(overridePath, out byte[]? local) || local.Length == 0)
                continue;

            files[standardPath] = TextContent.Append(standard, local);
        }

        return desired with { Files = files };
    }

    public ContentPlan Plan(
        DesiredRepository desired,
        IReadOnlyDictionary<string, byte[]> current,
        OrphanPolicy orphanPolicy)
    {
        byte[]? metadataBytes = current.GetValueOrDefault(MetadataPath);
        JsonObject? originalRoot = ParseMetadata(metadataBytes);
        string[] previousManaged = GetManagedFiles(originalRoot);
        HashSet<string> desiredPaths = new(desired.Files.Keys, StringComparer.Ordinal);
        string[] orphaned = previousManaged
            .Where(path => !desiredPaths.Contains(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (orphaned.Length > 0 && orphanPolicy == OrphanPolicy.Error)
        {
            return new ContentPlan([], orphaned, true,
                $"{orphaned.Length} previously managed file(s) require --orphaned-files keep or delete.");
        }

        List<FileOperation> operations = [];
        foreach ((string path, byte[] desiredBytes) in desired.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!current.TryGetValue(path, out byte[]? existing))
            {
                operations.Add(new(path, FileOperationKind.Add, desiredBytes));
                continue;
            }

            if (!TextContent.Normalize(existing).AsSpan().SequenceEqual(desiredBytes))
                operations.Add(new(path, FileOperationKind.Update, desiredBytes));
        }

        if (orphanPolicy == OrphanPolicy.Delete)
        {
            foreach (string path in orphaned)
            {
                if (current.ContainsKey(path))
                    operations.Add(new(path, FileOperationKind.Delete));
            }
        }

        if (desired.Files.Count == 0)
        {
            if (originalRoot is not null)
                operations.Add(new(MetadataPath, FileOperationKind.Delete));

            return new ContentPlan(SortOperations(operations), orphaned, false, null);
        }

        JsonObject metadata = BuildMetadata(originalRoot, desired);
        bool metadataChanged = originalRoot is null || !JsonNode.DeepEquals(originalRoot, metadata);
        bool updateTimestamp = operations.Count > 0 || metadataChanged;
        byte[] serializedMetadata = SerializeMetadata(metadata);
        if (!updateTimestamp && metadataBytes is not null &&
            !TextContent.Normalize(metadataBytes).AsSpan().SequenceEqual(serializedMetadata))
            updateTimestamp = true;

        if (updateTimestamp)
        {
            JsonObject meta = metadata["meta"]!.AsObject();
            meta["last_updated"] = _clock().ToString("O");
            serializedMetadata = SerializeMetadata(metadata);
        }

        if (metadataBytes is null)
            operations.Add(new(MetadataPath, FileOperationKind.Add, serializedMetadata));
        else if (!TextContent.Normalize(metadataBytes).AsSpan().SequenceEqual(serializedMetadata))
            operations.Add(new(MetadataPath, FileOperationKind.Update, serializedMetadata));

        return new ContentPlan(SortOperations(operations), orphaned, false, null);
    }

    private static byte[] SerializeMetadata(JsonObject metadata) =>
        TextContent.Normalize(Encoding.UTF8.GetBytes(metadata.ToJsonString(MetaJsonOptions) + "\n"));

    private static IReadOnlyList<FileOperation> SortOperations(IEnumerable<FileOperation> operations) =>
        operations
            .OrderBy(operation => operation.Path.Equals(MetadataPath, StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(operation => operation.Path, StringComparer.Ordinal)
            .ToArray();

    private static JsonObject BuildMetadata(JsonObject? existing, DesiredRepository desired)
    {
        JsonObject root = existing?.DeepClone().AsObject() ?? [];
        JsonObject meta = root["meta"] as JsonObject ?? [];
        root["$schema"] = MetadataSchema;
        meta["repo"] = desired.FullName;
        meta["profiles"] = new JsonArray(desired.Profiles.Select(profile => JsonValue.Create(profile)).ToArray());
        meta["files"] = new JsonArray(desired.Files.Keys.OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => JsonValue.Create(path)).ToArray());

        if (desired.MetaReference is not null)
        {
            if (desired.MetaReference.Length == 0)
                meta.Remove("reference");
            else
                meta["reference"] = desired.MetaReference;
        }

        root["meta"] = meta;
        return root;
    }

    private static JsonObject? ParseMetadata(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            JsonNode? node = JsonNode.Parse(bytes, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return node as JsonObject ?? throw new InvalidOperationException($"'{MetadataPath}' must contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Unable to parse existing '{MetadataPath}': {exception.Message}", exception);
        }
    }

    private static IEnumerable<string> TryGetManagedFiles(byte[]? metadata)
    {
        try
        {
            return GetManagedFiles(ParseMetadata(metadata));
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private static string[] GetManagedFiles(JsonObject? root)
    {
        if (root?["meta"]?["files"] is not JsonArray files)
            return [];

        List<string> paths = [];
        foreach (JsonNode? node in files)
        {
            string? path = node?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (!ManagedPath.TryNormalize(path, out string normalized, out string error))
                throw new InvalidOperationException($"Existing metadata contains unsafe path '{path}': {error}");
            paths.Add(normalized);
        }

        return paths.ToArray();
    }
}
