using System;
using System.IO;
using System.Linq;

namespace Mumblr.App.Updates;

/// <summary>
/// Puts the installed mumblr on the user's PATH, and takes it off again on uninstall.
///
/// The whole product is a command you type in a repo folder - `mumblr .` - and neither the
/// installer nor an unzipped portable build gave you that command. Velopack does not touch PATH,
/// so the app has to.
/// </summary>
internal static class PathRegistration
{
    /// <summary>
    /// The entry is the stub directory, never <c>current</c>: Velopack replaces <c>current</c> on
    /// every update, and a PATH entry pointing into it would break the moment one landed.
    /// </summary>
    public static string? ResolveStubDirectory(string applicationDirectory, Func<string, bool> fileExists)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(applicationDirectory));
        if (string.IsNullOrEmpty(parent))
            return null;

        return fileExists(Path.Combine(parent, "mumblr.exe")) ? parent : null;
    }

    /// <summary>Adds the directory once. Returns the new value, or null when nothing changed.</summary>
    public static string? WithEntry(string? path, string directory)
    {
        var entries = Split(path);
        if (entries.Any(entry => Matches(entry, directory)))
            return null;

        entries.Add(directory);
        return string.Join(';', entries);
    }

    /// <summary>Removes every occurrence. Returns the new value, or null when nothing changed.</summary>
    public static string? WithoutEntry(string? path, string directory)
    {
        var entries = Split(path);
        var kept = entries.Where(entry => !Matches(entry, directory)).ToList();

        return kept.Count == entries.Count ? null : string.Join(';', kept);
    }

    private static System.Collections.Generic.List<string> Split(string? path) =>
        (path ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>
    /// Windows paths are case insensitive, and a trailing separator means the same folder.
    /// The separators are trimmed by hand rather than with Path.TrimEndingDirectorySeparator,
    /// which only knows the separator of the platform it is running on - and this logic is
    /// exercised on Linux, against Windows paths.
    /// </summary>
    private static bool Matches(string entry, string directory) =>
        string.Equals(Trim(entry), Trim(directory), StringComparison.OrdinalIgnoreCase);

    private static string Trim(string path) => path.TrimEnd('\\', '/');

    public static void Register()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Apply(directory => WithEntry(Read(), directory));
    }

    public static void Unregister()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Apply(directory => WithoutEntry(Read(), directory));
    }

    private static void Apply(Func<string, string?> change)
    {
        try
        {
            var directory = ResolveStubDirectory(AppContext.BaseDirectory, File.Exists);
            if (directory is null)
                return;

            if (change(directory) is { } updated)
                Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
        }
        catch (Exception)
        {
            // An installer hook that throws leaves a half-installed app behind. A missing PATH
            // entry is a documented manual step; a failed install is not.
        }
    }

    private static string? Read() =>
        Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
}
