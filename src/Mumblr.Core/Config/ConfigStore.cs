using System.Text.Json;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// The text last read from or written to the file. Every window saves the whole config -
    /// picking a microphone is a save - so a watcher needs to tell a change by another window from
    /// the echo of its own write.
    /// </summary>
    private string? lastSeen;

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
            var raw = File.ReadAllText(ConfigPath);
            lastSeen = raw;

            var config = Parse(raw);

            if (ConfigMigration.Apply(config))
                TrySave(config);

            return config;
        }
        catch (Exception)
        {
            // A broken config must never stop the app from recording. It is left on disk exactly
            // as it is: the next Save overwrites it, and until then the file is the user's to fix.
            return new MumblrConfig();
        }
    }

    /// <summary>
    /// Nulls are dropped before deserialization, so a hand-edited <c>"sttMode": null</c> costs
    /// that one key its default instead of throwing - which used to cost the whole file, silently,
    /// because the throw was indistinguishable from a broken config. Applies to every value type,
    /// every list entry and every dictionary value, at any depth.
    /// </summary>
    private static MumblrConfig Parse(string json)
    {
        var node = JsonNode.Parse(json);
        StripNulls(node);
        return node.Deserialize<MumblrConfig>(Options) ?? new MumblrConfig();
    }

    private static void StripNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList())
                    o.Remove(key);
                foreach (var value in o.Select(pair => pair.Value))
                    StripNulls(value);
                break;

            case JsonArray a:
                for (var i = a.Count - 1; i >= 0; i--)
                    if (a[i] is null)
                        a.RemoveAt(i);
                    else
                        StripNulls(a[i]);
                break;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // The move already failed; a leftover temp file is not what the caller needs to hear.
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

        // Named per process: two mumblr windows share this file, and one temp name would let them
        // write the same one and move a half-finished file into place.
        var temporary = $"{ConfigPath}.{Environment.ProcessId}.tmp";
        var json = JsonSerializer.Serialize(config, Options);
        File.WriteAllText(temporary, json);

        try
        {
            File.Move(temporary, ConfigPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        lastSeen = json;
    }

    /// <summary>
    /// True when the file holds something other than what this store last read or wrote - which is
    /// the only interesting case for a watcher, since the app's own saves fire the same events.
    /// </summary>
    public bool ChangedOnDisk()
    {
        try
        {
            return File.ReadAllText(ConfigPath) != lastSeen;
        }
        catch (Exception)
        {
            // Gone, locked, or caught mid-move. Nothing to reload from right now, and the write
            // that is landing raises its own event.
            return false;
        }
    }
}
