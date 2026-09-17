using Mumblr.Core.Config;

namespace Mumblr.Core.Prompts;

/// <summary>
/// Puts the shipped prompts on disk the first time, and moves an existing install's
/// <see cref="MumblrConfig.PrebuiltCommands"/> out of config.json on the way.
/// </summary>
public static class PromptSeeding
{
    /// <summary>
    /// Seeds the directory when it does not exist, and only then. The trigger is deliberately the
    /// directory rather than it being empty: a prompt you deleted stays deleted, and deleting the
    /// whole directory is how you ask for the shipped ones back.
    ///
    /// The text comes from the config when it still holds entries, so a prompt the user edited
    /// survives the move as they left it. An unedited one was already replaced by the current
    /// shipped text on load - that is what <see cref="ConfigMigration"/> is for - so it arrives
    /// current without this having to know which is which.
    ///
    /// True when the config changed and the caller should save it.
    /// </summary>
    public static bool SeedIfMissing(PromptLibrary library, MumblrConfig config)
    {
        if (Directory.Exists(library.Directory))
            return false;

        var source = config.PrebuiltCommands is { Count: > 0 }
            ? config.PrebuiltCommands
            : new MumblrConfig().PrebuiltCommands;

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = 10;

        foreach (var command in source)
        {
            if (string.IsNullOrWhiteSpace(command.Label) || string.IsNullOrWhiteSpace(command.Text))
                continue;

            library.Write(Unique(FileName(command.Label), taken), command.Label.Trim(), order, command.Text.Trim());
            order += 10;
        }

        // The buttons come from the files now. Leaving the entries behind would mean editing them
        // in the place the Config button opens and watching nothing happen.
        config.PrebuiltCommands = [];
        return true;
    }

    /// <summary>A label as a file name: lowercase, no spaces, nothing a file system would refuse.</summary>
    private static string FileName(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(label.Trim().ToLowerInvariant()
            .Select(c => char.IsWhiteSpace(c) || Array.IndexOf(invalid, c) >= 0 ? '-' : c)
            .ToArray())
            .Trim('-');

        return name.Length == 0 ? "prompt" : name;
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        if (taken.Add(name))
            return name;

        for (var i = 2; ; i++)
        {
            var candidate = $"{name}-{i}";
            if (taken.Add(candidate))
                return candidate;
        }
    }
}
