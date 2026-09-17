using Mumblr.Core.Config;

namespace Mumblr.Core.Prompts;

/// <summary>
/// Puts the shipped prompts on disk the first time, and moves an existing install's
/// <see cref="MumblrConfig.PrebuiltCommands"/> out of config.json on the way.
/// </summary>
public static class PromptSeeding
{
    /// <summary>
    /// Windows refuses these as file names whatever the extension, and it refuses them by writing
    /// to a device instead of throwing - a prompt called "Con" would vanish without an error.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A file name long enough for any label and short enough for any path.</summary>
    private const int MaxNameLength = 60;

    /// <summary>
    /// Seeds the directory when it does not exist, and only then. The trigger is deliberately the
    /// directory rather than it being empty: a prompt you deleted stays deleted, and deleting the
    /// whole directory is how you ask for the shipped ones back on the next start.
    ///
    /// The text comes from the config when it still holds entries, so a prompt the user edited
    /// survives the move as they left it. An unedited one was already replaced by the current
    /// shipped text on load - that is what <see cref="ConfigMigration"/> is for - so it arrives
    /// current without this having to know which is which. A list that is there but empty means
    /// somebody wanted no buttons, and is honoured; only a missing one seeds the shipped two.
    ///
    /// All or nothing: the files are written into a sibling directory and moved into place, so a
    /// write that fails part way leaves nothing behind and the next start tries again. A partial
    /// directory would end the migration forever, with the entries still in config.json and
    /// nothing left that reads them.
    ///
    /// True when the config changed and the caller should save it.
    /// </summary>
    public static bool SeedIfMissing(PromptLibrary library, MumblrConfig config)
    {
        if (Directory.Exists(library.Directory))
            return false;

        var source = config.PrebuiltCommands ?? (IReadOnlyList<PrebuiltCommand>)MumblrConfig.FreshPrompts();

        var staging = $"{library.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}.{Environment.ProcessId}.tmp";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);

        try
        {
            var into = new PromptLibrary(staging);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var order = 10;

            Directory.CreateDirectory(staging);

            foreach (var command in source)
            {
                if (string.IsNullOrWhiteSpace(command.Label) || string.IsNullOrWhiteSpace(command.Text))
                    continue;

                into.Write(Unique(FileName(command.Label), taken), command.Label.Trim(), order, command.Text.Trim());
                order += 10;
            }

            // No CreateDirectory for the parent: the staging directory above already created it,
            // and Path.GetDirectoryName of a path ending in a separator returns the target itself -
            // which would create the destination and make the move below fail every time.
            Directory.Move(staging, library.Directory);
        }
        catch (Exception)
        {
            TryDelete(staging);

            // Another window seeded first - Directory.Move refuses an existing destination rather
            // than merging into it - and the prompts are on disk, which is all this was for, so
            // the entries still have to leave the config. Reporting a failure there would warn
            // about nothing and write the dead key back on the next save, permanently.
            //
            // The destination existing is not enough to believe that. Anything can create an empty
            // folder in the moment between the check at the top and the move: a sync client putting
            // back a directory somebody deleted, a backup, the user. Clearing the config over an
            // empty folder would delete the prompts and then never try again, because the folder
            // is what says the migration has run.
            if (!AnotherWriterWon(library.Directory, source.Count))
                throw;
        }

        // The buttons come from the files now. Leaving the entries behind would mean editing them
        // in the place the Config button opens and watching nothing happen.
        config.PrebuiltCommands = null;
        return true;
    }

    /// <summary>
    /// Whether a move that failed can be read as somebody else having seeded first. The
    /// destination has to hold prompts, not merely exist: anything can create an empty folder in
    /// the window between the check at the top and the move at the end - a sync client putting
    /// back a directory somebody deleted, a backup, the user - and clearing the config over one
    /// would delete the prompts and never try again, because the folder is what says the
    /// migration has run.
    /// </summary>
    /// <param name="directory">Where the prompts were meant to land.</param>
    /// <param name="expected">How many entries were being written; zero has nothing to lose.</param>
    public static bool AnotherWriterWon(string directory, int expected) =>
        Directory.Exists(directory)
        && (expected == 0 || Directory.EnumerateFiles(directory, "*.md").Any());

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // The seeding already failed; a leftover staging directory is not the news.
        }
    }

    /// <summary>A label as a file name: lowercase, no spaces, nothing a file system would refuse.</summary>
    private static string FileName(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();

        // A label that already ends in .md must not produce the same file as the one that does
        // not: Write appends the extension only when it is missing, so "Grammar.md" and "Grammar"
        // would both land in grammar.md and the first would be gone.
        var bare = label.Trim();
        while (bare.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            bare = bare[..^3];

        var name = new string(bare.ToLowerInvariant()
            .Select(c => char.IsWhiteSpace(c) || Array.IndexOf(invalid, c) >= 0 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\\' or '/' ? '-' : c)
            .ToArray())
            .Trim('-', '.', ' ');

        if (name.Length > MaxNameLength)
        {
            // Never between the halves of a surrogate pair: a label in an alphabet outside the
            // basic plane would otherwise end in half a character.
            var cut = MaxNameLength;
            if (char.IsHighSurrogate(name[cut - 1]))
                cut--;

            name = name[..cut].TrimEnd('-', '.', ' ');
        }

        // Windows applies the device rule to the segment before the first dot - con.txt.md is the
        // console just as con.md is - so the guard goes in front of it, not after the whole name.
        if (name.Length == 0)
            name = "prompt";
        else if (ReservedNames.Contains(name.Split('.')[0]))
            name = "prompt-" + name;

        return name;
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
