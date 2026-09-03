using System.Text.Json;

namespace Mumblr.Core.Config;

/// <summary>Loads and saves <see cref="MumblrConfig"/> as a single JSON file.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public ConfigStore(string configPath) => ConfigPath = configPath;

    public string ConfigPath { get; }

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "mumblr",
        "config.json");

    public static ConfigStore Default() => new(DefaultConfigPath);

    public MumblrConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var fresh = new MumblrConfig();
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<MumblrConfig>(json, Options) ?? new MumblrConfig();
        }
        catch (JsonException)
        {
            // A broken config must never stop the app from recording.
            return new MumblrConfig();
        }
    }

    public void Save(MumblrConfig config)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, Options));
    }
}
