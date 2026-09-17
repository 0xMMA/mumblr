using System.Globalization;
using System.Text;

namespace Mumblr.Core.Prompts;

/// <summary>
/// The prompts the command buttons send, as markdown files in the user's own directory.
///
/// One directory, and it is never configurable and never inside a repository. A prompt is handed
/// to `claude -p` with Read and Edit over the dictation file, so a repository-local
/// `.mumblr/prompts/` would let any cloned repo run its own instructions against whatever you
/// dictate. There is no opt-in for that, which is why this class takes its directory from the
/// caller only so the tests can have one - the app itself has exactly one source,
/// <see cref="DefaultDirectory"/>.
///
/// A file that cannot be read or parsed costs its own button and nothing else: the rest of the
/// library loads, and the caller reports what was skipped.
/// </summary>
public sealed class PromptLibrary
{
    /// <summary>Files without an explicit order sort after every file that has one, then by label.</summary>
    public const int NoOrder = int.MaxValue;

    public PromptLibrary(string directory) => Directory = directory;

    public string Directory { get; }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "mumblr",
        "prompts");

    public static PromptLibrary Default() => new(DefaultDirectory);

    /// <summary>
    /// Every readable prompt in the directory, ordered. The result also names the files that were
    /// skipped, so the window can say so rather than quietly showing fewer buttons.
    /// </summary>
    public PromptLoadResult Load()
    {
        if (!System.IO.Directory.Exists(Directory))
            return new PromptLoadResult([], []);

        var prompts = new List<PromptFile>();
        var skipped = new List<SkippedPrompt>();

        // Sorted so the order is the same on every platform before the frontmatter gets a say.
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.md").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var prompt = Parse(Read(path), path);
                if (prompt is null)
                    skipped.Add(new SkippedPrompt(path, "holds no prompt"));
                else
                    prompts.Add(prompt);
            }
            catch (DecoderFallbackException)
            {
                // A file saved as Latin-1 by an older editor. Saying "holds no prompt" would send
                // its owner looking for missing text instead of at the encoding.
                skipped.Add(new SkippedPrompt(path, "is not UTF-8"));
            }
            catch (Exception)
            {
                // Locked, or gone since the listing.
                skipped.Add(new SkippedPrompt(path, "could not be read"));
            }
        }

        return new PromptLoadResult(
            prompts
                .OrderBy(p => p.Order)
                .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            skipped);
    }

    /// <summary>
    /// Writes a prompt as a file. The label and order go into the frontmatter rather than into the
    /// file name, so renaming a file does not rename a button.
    /// </summary>
    public string Write(string fileName, string label, int order, string text)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var path = Path.Combine(Directory, fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".md");

        var content = new StringBuilder()
            .Append("---\n")
            // One line, whatever came in: a label is a word on a button, and a second line of it
            // would be read back as a frontmatter key that is not one and dropped.
            .Append("label: ").Append(OneLine(label)).Append('\n')
            .Append("order: ").Append(order.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("---\n\n")
            .Append(text.TrimEnd())
            .Append('\n')
            .ToString();

        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Read as strict UTF-8. The default decoder turns bad bytes into replacement characters, and
    /// a prompt is an instruction that gets executed over the user's text - a file this cannot
    /// read should lose its button and be named, not be sent in half.
    ///
    /// Byte order mark detection stays off on purpose: StreamReader replaces the encoding it was
    /// given the moment it sees one, with the forgiving UTF8 instance - so the strictness would
    /// hold for exactly the files a Windows editor does not produce. The UTF-8 mark decodes fine
    /// and is trimmed; anything else is not UTF-8 and is meant to fail here.
    /// </summary>
    private static string Read(string path)
    {
        using var reader = new StreamReader(
            path,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false);

        return reader.ReadToEnd().TrimStart('\uFEFF');
    }

    /// <summary>
    /// Frontmatter is the first line being <c>---</c>, then <c>key: value</c> lines, then a closing
    /// <c>---</c>. Unknown keys, comments and anything else in there are ignored rather than
    /// refused: a prompt is a text file someone edits by hand, and a stray line should not cost
    /// them the button.
    ///
    /// What makes the block frontmatter at all is that it names <c>label</c> or <c>order</c>. A
    /// pair of markdown rules is not frontmatter, however its lines are punctuated - a prompt that
    /// opens with a rule, or with a line like "Rule: keep it short", keeps everything it says.
    /// A block that names neither key would do nothing anyway, so reading it as text costs nothing
    /// and saves the case where it is text.
    /// </summary>
    private static PromptFile? Parse(string content, string path)
    {
        var label = Path.GetFileNameWithoutExtension(path);
        var order = NoOrder;
        var body = content;

        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
            if (end > 0 && lines[1..end].Any(NamesAKey))
            {
                for (var i = 1; i < end; i++)
                {
                    var separator = lines[i].IndexOf(':');
                    if (separator <= 0)
                        continue;

                    var key = lines[i][..separator].Trim();
                    var value = lines[i][(separator + 1)..].Trim();

                    if (key.Equals("label", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                        label = value;
                    else if (key.Equals("order", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        order = parsed;
                }

                body = string.Join('\n', lines[(end + 1)..]);
            }
        }

        body = body.Trim();

        // An empty prompt is not a prompt. Sending one would hand claude the header and no task.
        return body.Length == 0 ? null : new PromptFile(label, order, body, path);
    }

    /// <summary>One of the two keys this understands, which is what tells frontmatter from text.</summary>
    private static bool NamesAKey(string line)
    {
        var separator = line.IndexOf(':');
        if (separator <= 0)
            return false;

        var key = line[..separator].Trim();
        return key.Equals("label", StringComparison.OrdinalIgnoreCase)
            || key.Equals("order", StringComparison.OrdinalIgnoreCase);
    }

    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()));
}

/// <summary>A file that got no button, and the reason to put in front of its owner.</summary>
/// <param name="Path">The file.</param>
/// <param name="Reason">Reads after the file name: "notes.md holds no prompt".</param>
public sealed record SkippedPrompt(string Path, string Reason);

/// <summary>What <see cref="PromptLibrary.Load"/> found, and what it had to leave out.</summary>
/// <param name="Prompts">In the order the buttons should appear.</param>
/// <param name="Skipped">Files that could not be read or held no prompt, and why.</param>
public sealed record PromptLoadResult(IReadOnlyList<PromptFile> Prompts, IReadOnlyList<SkippedPrompt> Skipped);
