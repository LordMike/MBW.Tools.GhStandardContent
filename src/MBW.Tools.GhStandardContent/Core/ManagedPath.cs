namespace MBW.Tools.GhStandardContent.Core;

internal static class ManagedPath
{
    private static readonly char[] InvalidPortableCharacters = ['<', '>', ':', '"', '|', '?', '*'];

    public static bool TryNormalize(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The path is empty.";
            return false;
        }

        string candidate = value.Replace('\\', '/');
        if (candidate.StartsWith('/') || Path.IsPathRooted(candidate))
        {
            error = "Absolute paths are not allowed.";
            return false;
        }

        string[] segments = candidate.Split('/');
        if (segments.Any(segment =>
                segment.Length == 0 || segment is "." or ".." ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            error = "Empty, traversal, and .git path segments are not allowed.";
            return false;
        }

        if (candidate.Any(character => char.IsControl(character) || InvalidPortableCharacters.Contains(character)))
        {
            error = "The path contains characters that are unsafe across supported platforms.";
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    public static string ResolveBelowRoot(string rootPath, string managedPath)
    {
        string root = Path.GetFullPath(rootPath);
        string fullPath = Path.GetFullPath(Path.Combine(root, managedPath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(prefix, comparison))
            throw new InvalidOperationException($"Managed path '{managedPath}' escapes the repository root.");

        EnsureNoLinks(root, fullPath);
        if (File.Exists(fullPath))
        {
            FileInfo file = new(fullPath);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.LinkTarget is not null)
                throw new InvalidOperationException($"Managed path targets linked file '{fullPath}'.");
        }
        return fullPath;
    }

    private static void EnsureNoLinks(string root, string fullPath)
    {
        string? current = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(current) && current.Length >= root.Length)
        {
            if (Directory.Exists(current))
            {
                DirectoryInfo directory = new(current);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
                    throw new InvalidOperationException($"Managed path traverses linked directory '{current}'.");
            }

            if (string.Equals(current, root, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
                break;

            current = Path.GetDirectoryName(current);
        }
    }
}
