using System;
using System.IO;

namespace Mumblr.App.Config;

/// <summary>
/// Reports that something touched the files the window reads from the user's own directory: the
/// shared config.json, and the prompt files. `mumblr .` is meant to be run once per repo folder,
/// so several windows share both - without this, a microphone picked in the second window reached
/// the first only on a restart, and the last writer silently won.
/// </summary>
public sealed class FileChangeWatcher : IDisposable
{
    private readonly Action changed;
    private FileSystemWatcher? watcher;

    /// <param name="directory">Watched as it is. A directory that does not exist is watched by nobody.</param>
    /// <param name="filter">One file name, or a pattern such as <c>*.md</c>.</param>
    /// <param name="changed">
    /// Raised on a worker thread, possibly several times for one write. Callers marshal it and
    /// check whether anything actually differs - the app's own writes fire these events too.
    /// </param>
    public FileChangeWatcher(string directory, string filter, Action changed)
    {
        this.changed = changed;

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(filter) || !Directory.Exists(directory))
            return;

        try
        {
            watcher = new FileSystemWatcher(directory, filter)
            {
                // The config is written to a sibling file and moved into place, so the event that
                // matters is the rename onto the name - a plain write to it may never happen. An
                // editor saving a prompt does the same thing.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.Error += OnError;
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
    }

    /// <summary>
    /// False once the watcher has given up - the buffer overflowed, or the directory went away.
    /// The window promises that a change in another window arrives by itself, so the one way that
    /// promise can die quietly is worth being able to ask about.
    /// </summary>
    public bool IsWatching => watcher is { EnableRaisingEvents: true };

    public void Dispose()
    {
        var current = watcher;
        watcher = null;
        if (current is null)
            return;

        // Unsubscribed before the dispose, so a handler already on a thread-pool thread cannot
        // call back into a view model that is being torn down.
        current.Changed -= OnChanged;
        current.Created -= OnChanged;
        current.Deleted -= OnChanged;
        current.Renamed -= OnChanged;
        current.Error -= OnError;

        try
        {
            current.EnableRaisingEvents = false;
        }
        catch (Exception)
        {
            // Already dead, which is what we wanted.
        }

        current.Dispose();
    }

    private void OnChanged(object? sender, FileSystemEventArgs e) => changed();

    /// <summary>
    /// The watcher disables itself on an internal error, so the change that raised it is the last
    /// one this session sees. Reported as one more change: the caller reloads, notices nothing new,
    /// and can ask <see cref="IsWatching"/> whether it is still being told about them.
    /// </summary>
    private void OnError(object? sender, ErrorEventArgs e) => changed();
}
