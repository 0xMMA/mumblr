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
    /// Written last, and only when every prompt is on disk. This - not the directory - is what says
    /// the entries have been written out, because anything can create a directory: a sync client
    /// putting one back, a backup, the user. Four separate ways to lose somebody's prompts came out
    /// of asking the directory instead.
    /// </summary>
    private const string MarkerName = ".seeded";

    /// <summary>True once the entries have been written out and the config key may be dropped.</summary>
    public static bool HasRun(string directory) => File.Exists(Path.Combine(directory, MarkerName));

    /// <summary>
    /// Writes the prompt files unless that has already happened. The text comes from the config
    /// when it still holds entries, so a prompt the user edited survives the move as they left it.
    /// An unedited one was already replaced by the current shipped text on load - that is what
    /// <see cref="ConfigMigration"/> is for - so it arrives current without this having to know
    /// which is which. A list that is there but empty means somebody wanted no buttons and is
    /// honoured; only a missing one seeds the shipped two.
    ///
    /// Safe to run again after a failure, and it has to be: a write that throws leaves the marker
    /// unwritten, so the next start finishes the job. A file that is already there is never
    /// overwritten; it is kept when it already holds exactly the prompt this entry would write,
    /// and otherwise the entry goes to the next free name. Every entry therefore has a file of its
    /// own by the time the marker goes down, which is what makes dropping the config key safe.
    ///
    /// True when the config changed and the caller should save it.
    /// </summary>
    public static bool SeedIfMissing(PromptLibrary library, MumblrConfig config)
    {
        if (HasRun(library.Directory))
            return false;

        var source = config.PrebuiltCommands ?? (IReadOnlyList<PrebuiltCommand>)MumblrConfig.FreshPrompts();

        Directory.CreateDirectory(library.Directory);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = 10;

        foreach (var command in source)
        {
            if (string.IsNullOrWhiteSpace(command.Label) || string.IsNullOrWhiteSpace(command.Text))
                continue;

            var label = command.Label.Trim();
            var text = command.Text.Trim();
            var wanted = library.Render(label, order, text);
            var stem = FileName(label);

            // A file being there does not make it this prompt. It can be somebody else's, or this
            // migration's own from a run that was killed mid-write and left it half finished -
            // and skipping the entry over either of those, then dropping it from the config,
            // deletes a prompt that exists nowhere else. So: keep it if it already holds exactly
            // this, and otherwise write to the next free name rather than give the entry up. The
            // cost is a duplicate where a run was interrupted and the file it left was then
            // edited - a second button, visible and deletable, instead of a prompt that is gone.
            var name = Unique(stem, taken);
            var path = Path.Combine(library.Directory, name + ".md");
            while (File.Exists(path) && !PromptLibrary.Holds(path, wanted))
            {
                name = Unique(stem, taken);
                path = Path.Combine(library.Directory, name + ".md");
            }

            if (!File.Exists(path))
                library.Write(name, label, order, text);

            order += 10;
        }

        File.WriteAllText(
            Path.Combine(library.Directory, MarkerName),
            "The prompts in this folder were written by mumblr on first run. Delete this folder to\n"
            + "get the shipped ones back; deleting this file alone makes mumblr write the missing\n"
            + "ones again on the next start.\n");

        // The buttons come from the files now. Leaving the entries behind would mean editing them
        // in the place the Config button opens and watching nothing happen.
        config.PrebuiltCommands = null;
        return true;
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
