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
        var grammar = MumblrConfig.ShippedPrompts.Single(command => command.Label == "Grammar");

        grammar.Text.ShouldContain("Change nothing about the content");
        grammar.Text.ShouldContain("stays in the language it was dictated in");
        grammar.Text.ShouldNotContain("german", Case.Insensitive);
    }

    [Fact]
    public void The_shipped_prompt_command_shapes_without_inventing()
    {
        var prompt = MumblrConfig.ShippedPrompts.Single(command => command.Label == "Prompt");

        // The three things that make it safe on a dictation whose context the model cannot see.
        prompt.Text.ShouldContain("Open questions");
        prompt.Text.ShouldContain("add nothing the author did not say");
        prompt.Text.ShouldContain("language");
        prompt.Text.ShouldContain("no XML");
        prompt.Text.ShouldNotContain("german", Case.Insensitive);
    }

    [Fact]
    public void The_header_prompt_states_the_language_rule_without_naming_a_language()
    {
        var header = ClaudeConfig.DefaultHeaderPrompt;

        header.ShouldContain("language the file is in");
        // A spoken "translate this to English" is a command a user can give; the rule yields to it.
        header.ShouldContain("asks for a translation");
        header.ShouldNotContain("german", Case.Insensitive);
        header.ShouldNotContain("deutsch", Case.Insensitive);
    }

    [Fact]
    public void The_summary_follows_the_dictation_rather_than_the_window()
    {
        // The summary describes the author's text and sits beside it, so it is content about
        // content (#4): German dictation gets a German line in the log. Joined, because the raw
        // literal breaks lines wherever it likes.
        var header = string.Join(' ', ClaudeConfig.DefaultHeaderPrompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        header.ShouldContain("in the language the dictation was in before your edit");
        // The shipped buttons send English commands; the command's language must not decide it.
        header.ShouldContain("whatever language the command is in");
        header.ShouldNotContain("English sentence");
    }
}
