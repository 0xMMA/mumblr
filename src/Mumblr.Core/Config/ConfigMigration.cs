using System.Security.Cryptography;
using System.Text;

namespace Mumblr.Core.Config;

/// <summary>
/// The config file is written in full on first run, defaults included, so every install carries
/// a copy of whatever prompt shipped at the time - and keeps it through every update. A stored
/// value that still equals an older shipped default was never edited and follows the current
/// default. Anything else is the user's and stays.
///
/// When a shipped default changes, add the fingerprint of the text it replaces here in the same
/// commit, or no existing install ever sees the new one. <see cref="Fingerprint(string)"/> is
/// whitespace-insensitive, so the indentation of a raw string literal does not matter.
/// </summary>
public static class ConfigMigration
{
    /// <summary>Header prompts that shipped before the current one, oldest first.</summary>
    private static readonly HashSet<string> LegacyHeaderPrompts =
    [
        "739f135f7fd0565f41286a1059ad25e12f584e36d7f329a8bfa080013feea16b", // 0.1.0-0.1.4 "You are a prompt assistant for dictated text."
        "261ddd57fb11286ba823ffd222bcaa55f7ba577de6241521844539e257ca1718", // 0.1.5 "mumblr, a voice recorder, is calling you"
        "f86effd995a7d95dffc8645418d589bc0e1ff0e72add2990060d8fa65ff0548a", // 0.1.6 "The file holds dictated German"
    ];

    /// <summary>Prebuilt command lists that shipped before the current one.</summary>
    private static readonly HashSet<string> LegacyPrebuiltCommands =
    [
        "dc020f150a7e5b707451dbfbe03fee372ebcb7c4da450f9624523feb8ad7c3fd", // 0.1.3 Grammatik / Mach Grammatik, Satzbau ...
        "14d753baf07a808a40b0b2ae5f339298e137d882389cde231a601c5f39a04740", // 0.1.4-0.1.6 Grammar / Mach Grammatik, Satzbau ...
        "76fcd2710e6754d122e58c31f3929d65a79a81a490c255e21231dee23018f26b", // 0.2.0-pre Grammar (English) alone, before Prompt
    ];

    /// <summary>Replaces unedited legacy defaults with the current ones. True when anything changed.</summary>
    public static bool Apply(MumblrConfig config)
    {
        var changed = false;

        if (LegacyHeaderPrompts.Contains(Fingerprint(config.Claude.HeaderPrompt)))
        {
            config.Claude.HeaderPrompt = ClaudeConfig.DefaultHeaderPrompt;
            changed = true;
        }

        if (LegacyPrebuiltCommands.Contains(Fingerprint(config.PrebuiltCommands)))
        {
            config.PrebuiltCommands = new MumblrConfig().PrebuiltCommands;
            changed = true;
        }

        return changed;
    }

    public static string Fingerprint(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));

    public static string Fingerprint(IEnumerable<PrebuiltCommand> commands) =>
        Fingerprint(string.Join("\n", commands.Select(command => command.Label + "\n" + command.Text)));

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
