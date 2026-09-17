using System;
using System.IO;

namespace Mumblr.App.Config;

/// <summary>
/// Reports that something touched the shared config file. `mumblr .` is meant to be run once per
/// repo folder, so several windows write one %APPDATA%\mumblr\config.json: without this, the
/// microphone the second window picked reached the first only on a restart, and the last writer
/// silently won.
/// </summary>
public sealed class ConfigFileWatcher : IDisposable
{
    private readonly FileSystemWatcher? watcher;

    /// <param name="changed">
    /// Raised on a worker thread, possibly several times for one write. Callers marshal it and ask
    /// the store whether the contents actually differ - the app's own saves fire these events too.
    /// </param>
    public ConfigFileWatcher(string configPath, Action changed)
    {
        var directory = Path.GetDirectoryName(configPath);
        var name = Path.GetFileName(configPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name) || !Directory.Exists(directory))
            return;

        try
        {
            watcher = new FileSystemWatcher(directory, name)
            {
                // The config is written to a sibling file and moved into place, so the event that
                // matters is the rename onto the name - a plain write to it may never happen.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // A watcher is a convenience - the Reload button does the same thing by hand. Losing
            // it must not cost the window, so a platform that refuses one is simply watched by
            // nobody.
            watcher?.Dispose();
            watcher = null;
        }

        void OnChanged(object? sender, FileSystemEventArgs e) => changed();
    }

    public void Dispose() => watcher?.Dispose();
}
