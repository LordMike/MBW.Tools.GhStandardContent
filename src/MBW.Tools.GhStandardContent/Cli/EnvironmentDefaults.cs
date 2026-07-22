using System.CommandLine.Parsing;

namespace MBW.Tools.GhStandardContent.Cli;

internal static class EnvironmentDefaults
{
    public const string Config = "GHSC_CONFIG";
    public const string Repositories = "GHSC_REPOSITORIES";
    public const string Local = "GHSC_LOCAL";
    public const string GitHubApi = "GHSC_GITHUB_API";
    public const string Proxy = "GHSC_PROXY";
    public const string Parallelism = "GHSC_PARALLELISM";
    public const string Branch = "GHSC_BRANCH";
    public const string CommitAuthor = "GHSC_COMMIT_AUTHOR";
    public const string CommitEmail = "GHSC_COMMIT_EMAIL";
    public const string Labels = "GHSC_LABELS";
    public const string MetaReference = "GHSC_META_REFERENCE";
    public const string OrphanedFiles = "GHSC_ORPHANED_FILES";

    public static string[] GetList(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    public static string? GetOptionalString(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? GetString(string name) => Environment.GetEnvironmentVariable(name);

    public static string GetRequiredString(ArgumentResult result, string name, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null)
            return fallback;
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        result.AddError($"Environment variable {name} cannot be empty.");
        return fallback;
    }

    public static string GetRequiredArgument(ArgumentResult result, string argument, string environmentName)
    {
        string? value = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        result.AddError($"{argument} is required unless environment variable {environmentName} is set.");
        return string.Empty;
    }

    public static int GetInt(ArgumentResult result, string name, int fallback, int minimum, int maximum)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null)
            return fallback;
        if (int.TryParse(value, out int parsed) && parsed >= minimum && parsed <= maximum)
            return parsed;

        result.AddError($"Environment variable {name} must be an integer between {minimum} and {maximum}.");
        return fallback;
    }

    public static TEnum GetEnum<TEnum>(ArgumentResult result, string name, TEnum fallback)
        where TEnum : struct, Enum
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null)
            return fallback;
        if (Enum.TryParse(value, true, out TEnum parsed) && Enum.IsDefined(parsed))
            return parsed;

        string values = string.Join(", ", Enum.GetNames<TEnum>().Select(item => item.ToLowerInvariant()));
        result.AddError($"Environment variable {name} must be one of: {values}.");
        return fallback;
    }

    public static Uri GetRequiredUri(ArgumentResult result, string name, Uri fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null)
            return fallback;
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri))
            return uri;

        result.AddError($"Environment variable {name} must be an absolute URI.");
        return fallback;
    }

    public static Uri? GetOptionalUri(ArgumentResult result, string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri))
            return uri;

        result.AddError($"Environment variable {name} must be an absolute URI.");
        return null;
    }
}
