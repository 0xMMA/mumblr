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
        config.PrebuiltCommands.ShouldBeEmpty();
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

    public void Dispose() => TestDirectories.Delete(directory);
}
