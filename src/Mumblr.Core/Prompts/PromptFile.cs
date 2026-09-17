namespace Mumblr.Core.Prompts;

/// <summary>
/// One prompt read from disk: what its button says, where it sits among the others, and the text
/// that goes to claude.
/// </summary>
/// <param name="Label">What the button says. Falls back to the file name.</param>
/// <param name="Order">Ascending. Files without one sort after the ones that have it.</param>
/// <param name="Text">The prompt body, frontmatter removed.</param>
/// <param name="Path">Where it came from, so a broken one can be named in a warning.</param>
public sealed record PromptFile(string Label, int Order, string Text, string Path);
