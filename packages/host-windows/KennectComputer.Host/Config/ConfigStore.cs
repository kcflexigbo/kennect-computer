using System.Text.Json;
using System.Text.Json.Nodes;

namespace KennectComputer.Host.Config;

/// <summary>
/// Loads <c>config/default.json</c> and overlays the per-user config at
/// <c>%LOCALAPPDATA%\KennectComputer\config.json</c>. The
/// <c>KENNECT_COMPUTER_CONFIG</c> environment variable replaces the default
/// file path (used by tests and development runs).
///
/// The overlay is a property-wise deep merge: objects merge recursively,
/// arrays and scalars replace. A missing user config is normal; malformed JSON
/// fails with the offending path in the message.
/// </summary>
public static class ConfigStore
{
    /// <summary>Environment variable that replaces the default config path.</summary>
    public const string OverrideEnvironmentVariable = "KENNECT_COMPUTER_CONFIG";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads the default config, then the per-user override when present.</summary>
    public static AppConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadFrom(ResolveDefaultPath(path), UserConfigPath());
    }

    /// <summary>Applies the environment override to a default config path.</summary>
    internal static string ResolveDefaultPath(string path)
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overridePath)
            ? path
            : Path.GetFullPath(overridePath);
    }

    /// <summary>Load and merge with explicit paths; tests use this for isolation.</summary>
    internal static AppConfig LoadFrom(string defaultPath, string? userConfigPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultPath);

        var merged = ReadObject(defaultPath, required: true);
        if (!string.IsNullOrWhiteSpace(userConfigPath) && File.Exists(userConfigPath))
        {
            Merge(merged, ReadObject(userConfigPath, required: true));
        }

        try
        {
            return merged.Deserialize<AppConfig>(Options) ?? new AppConfig();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The Kennect Computer config is invalid: {exception.Message}",
                exception);
        }
    }

    /// <summary><c>%LOCALAPPDATA%\KennectComputer\config.json</c>.</summary>
    internal static string UserConfigPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KennectComputer",
            "config.json");

    /// <summary>
    /// Finds the default config beside a checkout: walks up from the host's
    /// output directory until <c>config/default.json</c> exists. Falls back to
    /// the path next to the executable.
    /// </summary>
    public static string FindDefaultPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "config", "default.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "config", "default.json");
    }

    private static JsonObject ReadObject(string path, bool required)
    {
        if (!File.Exists(path))
        {
            if (!required)
            {
                return new JsonObject();
            }

            throw new FileNotFoundException(
                $"The Kennect Computer config file was not found: {path}",
                path);
        }

        var json = File.ReadAllText(path);
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException(
                    $"The config file '{path}' must contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The config file '{path}' is not valid JSON: {exception.Message}",
                exception);
        }
    }

    /// <summary>Deep-merges <paramref name="overlay"/> into <paramref name="target"/>.</summary>
    private static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var pair in overlay)
        {
            var existingKey = target
                .Select(item => item.Key)
                .FirstOrDefault(key => string.Equals(key, pair.Key, StringComparison.OrdinalIgnoreCase));

            if (pair.Value is JsonObject overlayObject
                && existingKey is not null
                && target[existingKey] is JsonObject targetObject)
            {
                Merge(targetObject, overlayObject);
                continue;
            }

            target[existingKey ?? pair.Key] = pair.Value?.DeepClone();
        }
    }
}
