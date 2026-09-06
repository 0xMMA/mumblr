namespace Mumblr.Core.Documents;

/// <summary>
/// The markdown file for one mumblr run plus the WAV and the raw transcript that sit next to it.
/// Created the moment the app spawns so a Claude Code session can already reference the path.
/// </summary>
public sealed class DictationDocument
{
    private bool takePending;

    private DictationDocument(string markdownPath, string wavPath)
    {
        MarkdownPath = markdownPath;
        WavPath = wavPath;
        RawPath = Path.ChangeExtension(markdownPath, ".raw.md");
    }

    public string MarkdownPath { get; }

    public string WavPath { get; }

    /// <summary>
    /// What speech-to-text produced (after the dictionary pass), and only that. Commands rewrite
    /// the markdown in place and revert is one step at a time; this file is the words as spoken,
    /// whatever ran over them. Nothing an LLM wrote ever lands here - and the file is read-only
    /// on disk between appends, because Claude has Edit rights in this folder and a prompt is
    /// not a guarantee.
    /// </summary>
    public string RawPath { get; }

    public string RawText { get; private set; } = string.Empty;

    public string Directory => Path.GetDirectoryName(MarkdownPath)!;

    /// <summary>Creates <c>dictated-&lt;timestamp&gt;.md</c> in <paramref name="targetDirectory"/> right away.</summary>
    public static DictationDocument Create(string targetDirectory, DateTimeOffset? now = null)
    {
        var full = Path.GetFullPath(targetDirectory);
        System.IO.Directory.CreateDirectory(full);

        var stamp = (now ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss");
        var markdown = Path.Combine(full, $"dictated-{stamp}.md");
        var wav = Path.Combine(full, $"dictated-{stamp}.wav");

        // Collisions only happen when two instances start in the same second.
        var suffix = 1;
        while (File.Exists(markdown))
        {
            markdown = Path.Combine(full, $"dictated-{stamp}-{suffix}.md");
            wav = Path.Combine(full, $"dictated-{stamp}-{suffix}.wav");
            suffix++;
        }

        File.WriteAllText(markdown, string.Empty);
        return new DictationDocument(markdown, wav);
    }

    /// <summary>
    /// The next segment opens a new paragraph: a recording the user started, as opposed to channel
    /// 1 resuming after a command, which continues the take.
    /// </summary>
    public void BeginTake() => takePending = true;

    /// <summary>
    /// Appends one committed segment. The file appears on first use. Throws when the disk does;
    /// the in-memory mirror changes only after the write, so the two never disagree.
    /// </summary>
    public void AppendRaw(string text)
    {
        if (text.Length == 0)
            return;

        var separator = RawText.Length == 0 ? string.Empty
            : takePending ? "\n\n"
            : char.IsWhiteSpace(RawText[^1]) ? string.Empty
            : " ";
        var chunk = separator + text;

        if (File.Exists(RawPath))
            File.SetAttributes(RawPath, FileAttributes.Normal);
        File.AppendAllText(RawPath, chunk);
        File.SetAttributes(RawPath, FileAttributes.ReadOnly);

        takePending = false;
        RawText += chunk;
    }

    /// <summary>Writes the in-memory buffer to disk. Called on every state change and on copy.</summary>
    public void Flush(string text) => File.WriteAllText(MarkdownPath, text);

    /// <summary>Reads the file back into the buffer after <c>claude -p</c> edited it.</summary>
    public string Read() => File.Exists(MarkdownPath) ? File.ReadAllText(MarkdownPath) : string.Empty;
}
