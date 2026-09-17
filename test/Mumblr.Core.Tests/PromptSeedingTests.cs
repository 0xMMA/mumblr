using Mumblr.Core.Config;
using Mumblr.Core.Prompts;

namespace Mumblr.Core.Tests;

public class PromptSeedingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"mumblr-seed-{Guid.NewGuid():N}");

    private PromptLibrary Library => new(directory);

    [Fact]
    public void A_fresh_install_gets_the_shipped_prompts_as_files()
    {
        var config = new MumblrConfig();

        PromptSeeding.SeedIfMissing(Library, config).ShouldBeTrue();

        Library.Load().Prompts.Select(p => p.Label).ShouldBe(["Grammar", "Prompt"]);

        // Null, not empty: the key leaves config.json entirely, so nothing invites an edit in the
        // file the Config button opens that no longer has any effect.
        config.PrebuiltCommands.ShouldBeNull();
    }

    [Fact]
    public void A_prompt_the_user_edited_moves_across_as_they_left_it()
    {
        var config = new MumblrConfig
        {
            PrebuiltCommands = [new PrebuiltCommand { Label = "Shorter", Text = "Halve the length." }],
        };

        PromptSeeding.SeedIfMissing(Library, config);

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();
        prompt.Label.ShouldBe("Shorter");
        prompt.Text.ShouldBe("Halve the length.");
    }

    [Fact]
    public void The_order_of_the_buttons_survives_the_move()
    {
        var config = new MumblrConfig
        {
            PrebuiltCommands =
            [
                new PrebuiltCommand { Label = "Second", Text = "b" },
                new PrebuiltCommand { Label = "First", Text = "a" },
            ],
        };

        PromptSeeding.SeedIfMissing(Library, config);

        // "First" sorts after "Second" by name; the frontmatter is what keeps the order.
        Library.Load().Prompts.Select(p => p.Label).ShouldBe(["Second", "First"]);
    }

    [Fact]
    public void Two_prompts_with_the_same_label_do_not_share_a_file()
    {
        var config = new MumblrConfig
        {
            PrebuiltCommands =
            [
                new PrebuiltCommand { Label = "Fix", Text = "a" },
                new PrebuiltCommand { Label = "fix", Text = "b" },
            ],
        };

        PromptSeeding.SeedIfMissing(Library, config);

        Library.Load().Prompts.Select(p => p.Text).ShouldBe(["a", "b"]);
    }

    [Fact]
    public void A_label_that_is_not_a_file_name_still_gets_a_file()
    {
        var config = new MumblrConfig
        {
            PrebuiltCommands = [new PrebuiltCommand { Label = "Fix: grammar / style", Text = "a" }],
        };

        PromptSeeding.SeedIfMissing(Library, config);

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();
        prompt.Label.ShouldBe("Fix: grammar / style");
        Path.GetFileName(prompt.Path).ShouldNotContain("/");
    }

    [Fact]
    public void Seeding_happens_once_and_a_deleted_prompt_stays_deleted()
    {
        var config = new MumblrConfig();
        PromptSeeding.SeedIfMissing(Library, config);

        File.Delete(Library.Load().Prompts.First(p => p.Label == "Grammar").Path);

        PromptSeeding.SeedIfMissing(Library, config).ShouldBeFalse();
        Library.Load().Prompts.Select(p => p.Label).ShouldBe(["Prompt"]);
    }

    [Fact]
    public void Deleting_the_whole_directory_is_how_the_shipped_prompts_come_back()
    {
        var config = new MumblrConfig();
        PromptSeeding.SeedIfMissing(Library, config);
        TestDirectories.Delete(directory);

        PromptSeeding.SeedIfMissing(Library, config).ShouldBeTrue();

        Library.Load().Prompts.Select(p => p.Label).ShouldBe(["Grammar", "Prompt"]);
    }

    [Fact]
    public void Wanting_no_buttons_at_all_is_honoured()
    {
        // An empty list is a decision. Only a missing one means "this install has never been asked".
        var config = new MumblrConfig { PrebuiltCommands = [] };

        PromptSeeding.SeedIfMissing(Library, config).ShouldBeTrue();

        Library.Load().Prompts.ShouldBeEmpty();
        config.PrebuiltCommands.ShouldBeNull();
    }

    [Fact]
    public void A_label_windows_reserves_as_a_device_still_gets_a_file()
    {
        // con.md writes to the console on Windows: no file, no exception, no button, no warning.
        var config = new MumblrConfig
        {
            PrebuiltCommands = [new PrebuiltCommand { Label = "Con", Text = "a" }],
        };

        PromptSeeding.SeedIfMissing(Library, config);

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();
        prompt.Label.ShouldBe("Con");
        Path.GetFileNameWithoutExtension(prompt.Path).ToLowerInvariant().ShouldNotBe("con");
    }

    [Fact]
    public void A_very_long_label_does_not_become_a_very_long_path()
    {
        var config = new MumblrConfig
        {
            PrebuiltCommands = [new PrebuiltCommand { Label = new string('x', 300), Text = "a" }],
        };

        PromptSeeding.SeedIfMissing(Library, config);

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();
        Path.GetFileName(prompt.Path).Length.ShouldBeLessThan(80);
        prompt.Label.Length.ShouldBe(300);
    }

    [Fact]
    public void A_seeding_that_cannot_finish_leaves_nothing_behind_and_tries_again()
    {
        // A file where the directory should go: everything is written, and the move into place
        // fails. A half-filled directory would end the migration forever - Directory.Exists is
        // what stops it running again - with the entries still in a config nothing reads.
        File.WriteAllText(directory, "in the way");

        var config = new MumblrConfig
        {
            PrebuiltCommands = [new PrebuiltCommand { Label = "Shorter", Text = "Halve it." }],
        };

        Should.Throw<Exception>(() => PromptSeeding.SeedIfMissing(Library, config));

        config.PrebuiltCommands.ShouldNotBeNull();
        Directory.GetDirectories(Path.GetTempPath(), Path.GetFileName(directory) + ".*").ShouldBeEmpty();

        File.Delete(directory);
    }

    public void Dispose() => TestDirectories.Delete(directory);
}
