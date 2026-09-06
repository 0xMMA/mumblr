using System.Security.Cryptography;
using System.Text;

namespace Mumblr.Core.Config;

/// <summary>
/// What <see cref="ConfigStore.Load"/> does to a deserialized config before anyone uses it.
///
/// Nulls first: the Config button invites hand editing, and <c>"x": null</c> is what an editor
/// leaves behind. A null means the default for that key, and a null entry in a list is dropped;
/// nothing here throws.
///
/// Then the shipped defaults. The file is written in full on first run, defaults included, so
/// every install carries a copy of whatever prompt shipped at the time - and keeps it through
/// every update. A stored value that still equals an older shipped default was never edited and
/// follows the current default. Anything else is the user's and stays. When a shipped default
/// changes, add the fingerprint of the text it replaces here in the same commit, or no existing
/// install ever sees the new one; the fingerprint is whitespace-insensitive, so the indentation
/// of a raw string literal does not matter.
/// </summary>
public static class ConfigMigration
{
    /// <summary>Header prompts that shipped before the current one, oldest first. Unreleased ones too - a dev box runs those.</summary>
    private static readonly HashSet<string> LegacyHeaderPrompts =
    [
        "739f135f7fd0565f41286a1059ad25e12f584e36d7f329a8bfa080013feea16b", // 0.1.0-0.1.4 "You are a prompt assistant for dictated text."
        "261ddd57fb11286ba823ffd222bcaa55f7ba577de6241521844539e257ca1718", // 0.1.5 "mumblr, a voice recorder, is calling you"
        "f86effd995a7d95dffc8645418d589bc0e1ff0e72add2990060d8fa65ff0548a", // 0.1.6 "The file holds dictated German"
        "84a8df986008a764277ff623513586392dd0b0967753594b9d9b15f247bac245", // unreleased, between 0.1.6 and 0.2.0: language rule without the translation clause
    ];

    /// <summary>Prebuilt command lists that shipped before the current one.</summary>
    private static readonly HashSet<string> LegacyPrebuiltCommands =
    [
        "0d45fee38f31d3ec6abdb4752c41f85b65fef4aea1e04b63305f171edd10bbb7", // unreleased 0.1.3-pre: Grammatik / "... nichts aendern."
        "dc020f150a7e5b707451dbfbe03fee372ebcb7c4da450f9624523feb8ad7c3fd", // 0.1.3 Grammatik / Mach Grammatik, Satzbau ...
        "14d753baf07a808a40b0b2ae5f339298e137d882389cde231a601c5f39a04740", // 0.1.4-0.1.6 Grammar / Mach Grammatik, Satzbau ...
        "4537c9610250d9716a116db5363c505115170ff399cd7cd5e1f0081503402be4", // unreleased, between 0.1.6 and 0.2.0: Grammar (English, "technical terms included")
        "76fcd2710e6754d122e58c31f3929d65a79a81a490c255e21231dee23018f26b", // 0.2.0-pre Grammar (English) alone, before Prompt
    ];

    /// <summary>Repairs nulls and replaces unedited legacy defaults. True when anything changed.</summary>
    public static bool Apply(MumblrConfig config)
    {
        var changed = RepairNulls(config);

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

    private static bool RepairNulls(MumblrConfig config)
    {
        var defaults = new MumblrConfig();
        var changed = false;

        if (config.Claude is null) { config.Claude = defaults.Claude; changed = true; }
        if (config.Stt is null) { config.Stt = defaults.Stt; changed = true; }
        if (config.Hotkeys is null) { config.Hotkeys = defaults.Hotkeys; changed = true; }
        if (config.Keyterms is null) { config.Keyterms = defaults.Keyterms; changed = true; }
        if (config.Dictionary is null) { config.Dictionary = defaults.Dictionary; changed = true; }
        if (config.Stt.Languages is null) { config.Stt.Languages = defaults.Stt.Languages; changed = true; }
        if (config.Claude.HeaderPrompt is null) { config.Claude.HeaderPrompt = ClaudeConfig.DefaultHeaderPrompt; changed = true; }
        if (config.Claude.AllowedTools is null) { config.Claude.AllowedTools = defaults.Claude.AllowedTools; changed = true; }
        if (config.Claude.DisallowedTools is null) { config.Claude.DisallowedTools = defaults.Claude.DisallowedTools; changed = true; }
        if (config.Claude.ExtraArgs is null) { config.Claude.ExtraArgs = defaults.Claude.ExtraArgs; changed = true; }

        if (config.PrebuiltCommands is null)
        {
            config.PrebuiltCommands = defaults.PrebuiltCommands;
            changed = true;
        }
        else if (config.PrebuiltCommands.Any(command => command is null))
        {
            config.PrebuiltCommands = config.PrebuiltCommands.Where(command => command is not null).ToList();
            changed = true;
        }

        foreach (var command in config.PrebuiltCommands)
        {
            command.Label ??= string.Empty;
            command.Text ??= string.Empty;
        }

        return changed;
    }

    private static string Fingerprint(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));

    private static string Fingerprint(IEnumerable<PrebuiltCommand> commands) =>
        Fingerprint(string.Join("\n", commands.Select(command => command.Label + "\n" + command.Text)));

    private static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
