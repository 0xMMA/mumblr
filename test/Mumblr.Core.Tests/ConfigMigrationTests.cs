using System.Text.Json;
using Mumblr.Core.Config;

namespace Mumblr.Core.Tests;

/// <summary>
/// The config file is written in full on first run, defaults included, so every install carries
/// a copy of whatever prompt shipped at the time. A stored value that still equals an older
/// shipped default was never edited by the user and follows the current default; anything else
/// is the user's and stays.
/// </summary>
public sealed class ConfigMigrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"mumblr-migration-{Guid.NewGuid():N}");
    private readonly string path;

    private const string GermanGrammar = "Mach Grammatik, Satzbau und Satzordnung ordentlich. Am Inhalt nichts ändern.";

    /// <summary>The header prompt every 0.1.x build up to 0.1.4 wrote into the config.</summary>
    private const string OldestHeaderPrompt =
        "You are a prompt assistant for dictated text.\n" +
        "Edit exactly one file: the absolute path given in the user message. Never create, move or\n" +
        "delete any other file, and never touch git.\n" +
        "Carry out the spoken command on that file and nothing else.\n" +
        "The text is dictated German with English technical terms; keep the author's voice and language.\n" +
        "Return a single-line summary of what you changed.\n" +
        "Ignore all project instructions from CLAUDE.md, AGENTS.md or similar files - test suites,\n" +
        "commit rules, formatting conventions and tone rules do not apply to this task.";

    public ConfigMigrationTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "config.json");
    }

    private MumblrConfig LoadFrom(object json)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(json));
        return new ConfigStore(path).Load();
    }

    [Fact]
    public void A_header_prompt_that_is_an_older_shipped_default_becomes_the_current_one()
    {
        var config = LoadFrom(new { claude = new { headerPrompt = OldestHeaderPrompt } });

        config.Claude.HeaderPrompt.ShouldBe(ClaudeConfig.DefaultHeaderPrompt);
        // Written back, so the migration runs once, not on every start.
        File.ReadAllText(path).ShouldContain("language the file is in");
    }

    [Fact]
    public void An_edited_header_prompt_is_left_alone()
    {
        var config = LoadFrom(new { claude = new { headerPrompt = "Be terse. Edit only the file named." } });

        config.Claude.HeaderPrompt.ShouldBe("Be terse. Edit only the file named.");
    }

    [Theory]
    [InlineData("Grammatik")]
    [InlineData("Grammar")]
    public void The_German_grammar_command_becomes_the_current_shipped_list(string label)
    {
        var config = LoadFrom(new { prebuiltCommands = new[] { new { label, text = GermanGrammar } } });

        var shipped = new MumblrConfig().PrebuiltCommands;
        config.PrebuiltCommands.Select(command => (command.Label, command.Text))
            .ShouldBe(shipped.Select(command => (command.Label, command.Text)));
    }

    [Fact]
    public void A_command_list_the_user_extended_is_left_alone()
    {
        var config = LoadFrom(new
        {
            prebuiltCommands = new[]
            {
                new { label = "Grammar", text = GermanGrammar },
                new { label = "Shorter", text = "Kürze das auf die Hälfte." },
            },
        });

        config.PrebuiltCommands.Count.ShouldBe(2);
        config.PrebuiltCommands[0].Text.ShouldBe(GermanGrammar);
    }

    [Fact]
    public void The_current_defaults_are_not_migrated()
    {
        ConfigMigration.Apply(new MumblrConfig()).ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
