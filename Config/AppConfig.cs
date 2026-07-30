using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReadOnlyDbMcp.Config;

public sealed class ConnectionConfig
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("connectionString")] public string? ConnectionString { get; set; }
    [JsonPropertyName("connectionStringEnv")] public string? ConnectionStringEnv { get; set; }
    // Opt-in: when absent, the get_view_definition tool is not offered for this connection.
    [JsonPropertyName("exposeViewDefinitions")] public bool ExposeViewDefinitions { get; set; }
}

public sealed class ConfigFile
{
    [JsonPropertyName("maxRows")] public int MaxRows { get; set; } = 1000;
    [JsonPropertyName("defaultLimit")] public int DefaultLimit { get; set; } = 100;
    [JsonPropertyName("commandTimeoutSeconds")] public int CommandTimeoutSeconds { get; set; } = 30;
    [JsonPropertyName("connections")]
    public Dictionary<string, ConnectionConfig> Connections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AppConfig
{
    public required ConfigFile File { get; init; }
    public required IReadOnlyList<string> ExposedNames { get; init; }
    public required string ConfigPath { get; init; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static (string? ConfigPath, List<string> Connections) ParseArgs(string[] args)
    {
        string? configPath = null;
        var exposed = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--connections" or "--connection" when i + 1 < args.Length:
                    exposed.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
            }
        }

        return (configPath, exposed);
    }

    public static string ResolveConfigPath(string? explicitPath) =>
        explicitPath
        ?? Environment.GetEnvironmentVariable("READONLYDB_CONFIG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".readonlydb", "config.json");

    public static ConfigFile ReadConfigFile(string configPath)
    {
        if (!System.IO.File.Exists(configPath))
            throw new InvalidOperationException(
                $"Config file not found at '{configPath}'. Run '{Cli.SetupCli.Invocation} init' to create it, pass --config <path>, or set READONLYDB_CONFIG.");

        return JsonSerializer.Deserialize<ConfigFile>(System.IO.File.ReadAllText(configPath), JsonOpts)
            ?? throw new InvalidOperationException($"Config file '{configPath}' is empty or invalid.");
    }

    public static AppConfig Load(string[] args)
    {
        var (explicitPath, exposed) = ParseArgs(args);
        var configPath = ResolveConfigPath(explicitPath);
        var file = ReadConfigFile(configPath);

        if (exposed.Count == 0)
            throw new InvalidOperationException(
                "No connections exposed. Pass --connections <name>[,<name>...] to allowlist which configured " +
                "connections this server instance serves. This is required by design: agents can only ever see " +
                "connections named here, regardless of what else exists in the config file.");

        foreach (var name in exposed)
            if (!file.Connections.ContainsKey(name))
                throw new InvalidOperationException($"Connection '{name}' is not defined in '{configPath}'.");

        return new AppConfig
        {
            File = file,
            ExposedNames = exposed.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ConfigPath = configPath,
        };
    }
}
