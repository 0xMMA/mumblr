namespace Mumblr.Core.Documents;

/// <summary>
/// The markdown file for one mumblr run plus the WAV that sits next to it. Created the moment the
/// app spawns so a Claude Code session can already reference the path.
/// </summary>
public sealed class DictationDocument
{
    private DictationDocument(string markdownPath, string wavPath)
    {
        MarkdownPath = markdownPath;
        WavPath = wavPath;
    }

    public string MarkdownPath { get; }

    public string WavPath { get; }

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

    /// <summary>Writes the in-memory buffer to disk. Called on every state change and on copy.</summary>
    public void Flush(string text) => File.WriteAllText(MarkdownPath, text);

    /// <summary>Reads the file back into the buffer after <c>claude -p</c> edited it.</summary>
    public string Read() => File.Exists(MarkdownPath) ? File.ReadAllText(MarkdownPath) : string.Empty;
}
