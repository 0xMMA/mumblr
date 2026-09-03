using System.Text.RegularExpressions;

namespace Mumblr.Core.Text;

/// <summary>
/// Deterministic client side micro-postprocessing. No LLM: a plain dictionary of replacements
/// applied to committed transcript text ("clod code" -> "Claude Code").
/// </summary>
public sealed class TextPostProcessor
{
    private readonly List<(Regex Pattern, string Replacement)> rules;

    public TextPostProcessor(IReadOnlyDictionary<string, string> dictionary)
    {
        // Longest first so "claude code" wins over a "code" rule.
        rules = dictionary
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => (BuildPattern(kv.Key), kv.Value))
            .ToList();
    }

    public string Apply(string text)
    {
        if (string.IsNullOrEmpty(text) || rules.Count == 0)
            return text;

        foreach (var (pattern, replacement) in rules)
            text = pattern.Replace(text, replacement.Replace("$", "$$"));

        return text;
    }

    private static Regex BuildPattern(string term)
    {
        // \b does not fire next to non-word characters, so only add boundaries where they help.
        var escaped = Regex.Escape(term.Trim());
        var prefix = char.IsLetterOrDigit(term.Trim()[0]) ? @"\b" : string.Empty;
        var suffix = char.IsLetterOrDigit(term.Trim()[^1]) ? @"\b" : string.Empty;
        return new Regex(prefix + escaped + suffix, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
