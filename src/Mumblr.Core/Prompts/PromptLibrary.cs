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
        var skipped = new List<string>();

        // Sorted so the order is the same on every platform before the frontmatter gets a say.
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.md").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var prompt = Parse(Read(path), path);
                if (prompt is null)
                    skipped.Add(path);
                else
                    prompts.Add(prompt);
            }
            catch (Exception)
            {
                // Locked, gone since the listing, or not UTF-8 - a file saved as Latin-1 would
                // otherwise decode into replacement characters and send garbled instructions.
                skipped.Add(path);
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
    /// </summary>
    private static string Read(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Frontmatter is the first line being <c>---</c>, then <c>key: value</c> lines, then a closing
    /// <c>---</c>. Unknown keys are ignored rather than refused: a prompt is a text file someone
    /// edits by hand, and a stray key should not cost them the button.
    ///
    /// Every line in between has to look like a pair, though. A prompt that opens with a markdown
    /// rule and has another one further down is not a prompt with frontmatter, and reading it as
    /// one would silently swallow everything above the second rule.
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
            if (end > 0 && lines[1..end].All(IsFrontmatterLine))
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

    private static bool IsFrontmatterLine(string line) =>
        line.Trim().Length == 0 || line.IndexOf(':') > 0;

    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()));
}

/// <summary>What <see cref="PromptLibrary.Load"/> found, and what it had to leave out.</summary>
/// <param name="Prompts">In the order the buttons should appear.</param>
/// <param name="Skipped">Paths that could not be read or held no prompt.</param>
public sealed record PromptLoadResult(IReadOnlyList<PromptFile> Prompts, IReadOnlyList<string> Skipped);
