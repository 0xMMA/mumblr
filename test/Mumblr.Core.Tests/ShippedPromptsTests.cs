using Mumblr.Core.Config;

namespace Mumblr.Core.Tests;

/// <summary>
/// The shipped prompts are instructions to Claude and therefore English, like every other
/// string that is not the user's content. The one thing an English instruction must never do
/// is pull the dictation into English with it, so the language rule is pinned here: a rewording
/// that drops it fails the build instead of silently translating someone's notes.
/// </summary>
public class ShippedPromptsTests
{
    [Fact]
    public void The_shipped_grammar_command_is_English_and_keeps_the_language_of_the_text()
    {
        var grammar = new MumblrConfig().PrebuiltCommands.Single(command => command.Label == "Grammar");

        grammar.Text.ShouldContain("language");
        grammar.Text.ShouldContain("content");
        grammar.Text.ShouldNotContain("Grammatik");
    }

    [Fact]
    public void The_header_prompt_states_the_language_rule_without_naming_a_language()
    {
        var header = ClaudeConfig.DefaultHeaderPrompt;

        header.ShouldContain("language the file is in");
        header.ShouldNotContain("German");
        header.ShouldNotContain("English technical terms");
    }
}
