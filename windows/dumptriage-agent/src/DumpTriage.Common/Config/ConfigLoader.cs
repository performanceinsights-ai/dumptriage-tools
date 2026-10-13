using System.Text.Json;
using System.Text.Json.Serialization;

namespace DumpTriage.Common.Config;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<AgentConfig> LoadOrCreateDefaultAsync(string configPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
        {
            var defaultConfig = new AgentConfig();
            await SaveAsync(configPath, defaultConfig, cancellationToken);
            return defaultConfig;
        }

        await using var stream = File.OpenRead(configPath);
        var config = await JsonSerializer.DeserializeAsync<AgentConfig>(stream, JsonOptions, cancellationToken);
        return config ?? new AgentConfig();
    }

    public static async Task SaveAsync(string configPath, AgentConfig config, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(configPath);
        await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
    }
}
