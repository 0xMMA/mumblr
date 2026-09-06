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
            TrySave(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<MumblrConfig>(json, Options) ?? new MumblrConfig();

            if (ConfigMigration.Apply(config))
                TrySave(config);

            return config;
        }
        catch (Exception ex) when (ex is not JsonException)
        {
            // Same rule as below, for whatever the repair or the write-back did not foresee.
            return new MumblrConfig();
        }
        catch (JsonException)
        {
            // A broken config must never stop the app from recording.
            return new MumblrConfig();
        }
    }

    /// <summary>A default or a migration that cannot be written back still applies in memory; the app starts.</summary>
    private void TrySave(MumblrConfig config)
    {
        try
        {
            Save(config);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Written to a sibling file and moved into place, so a second mumblr instance reading the
    /// config at the same moment sees the old file or the new one, never a truncated one - which
    /// would deserialize as broken, load as defaults, and be saved back over the user's settings.
    /// </summary>
    public void Save(MumblrConfig config)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, Options));

        try
        {
            File.Move(temporary, ConfigPath, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}
