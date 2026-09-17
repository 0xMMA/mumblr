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

    [Theory]
    [InlineData("CON.txt")]
    [InlineData("aux.1")]
    [InlineData("COM1.log")]
    [InlineData("nul.md")]
    public void A_reserved_name_with_an_extension_is_a_device_too(string label)
    {
        // Windows reads the segment before the first dot, so nul.tar.gz is the null device. A
        // guard appended to the end of the name leaves that segment exactly as it was.
        var config = new MumblrConfig
        {
            PrebuiltCommands = [new PrebuiltCommand { Label = label, Text = "a" }],
        };

        PromptSeeding.SeedIfMissing(Library, config);

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();
        var head = Path.GetFileName(prompt.Path).Split('.')[0].ToUpperInvariant();
        head.ShouldNotBeOneOf("CON", "PRN", "AUX", "NUL", "COM1", "LPT1");
    }

    [Fact]
    public void A_folder_that_is_simply_there_does_not_stop_the_migration()
    {
        // Anything can create it: a sync client putting back a directory somebody deleted, a
        // backup, the user. Four ways to lose somebody's prompts came out of reading an existing
        // directory as "already done", so what says that is a file this writes last.
        Directory.CreateDirectory(directory);

        var config = new MumblrConfig
        {
            PrebuiltCommands = [new PrebuiltCommand { Label = "Shorter", Text = "Halve it." }],
        };

        PromptSeeding.SeedIfMissing(Library, config).ShouldBeTrue();

        Library.Load().Prompts.ShouldHaveSingleItem().Label.ShouldBe("Shorter");
        config.PrebuiltCommands.ShouldBeNull();
    }

    [Fact]
    public void Somebody_elses_file_in_the_folder_costs_nothing()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "theirs.md"), "Their prompt.\n");

        var config = new MumblrConfig
        {
            PrebuiltCommands = [new PrebuiltCommand { Label = "Shorter", Text = "Halve it." }],
        };

        PromptSeeding.SeedIfMissing(Library, config);

        Library.Load().Prompts.Select(p => p.Label).ShouldBe(["Shorter", "theirs"]);
    }

    [Fact]
    public void A_file_that_is_already_there_is_never_overwritten()
    {
        // Either the user's, or this migration's own from a run that stopped part way. Both are
        // the content that belongs there; neither wants the shipped text on top of it.
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "grammar.md"), "Mine, thanks.\n");

        PromptSeeding.SeedIfMissing(Library, new MumblrConfig());

        var prompts = Library.Load().Prompts;
        prompts.Single(p => Path.GetFileName(p.Path) == "grammar.md").Text.ShouldBe("Mine, thanks.");
        prompts.Select(p => p.Label).ShouldContain("Prompt");
    }

    [Fact]
    public void A_seeding_that_cannot_finish_keeps_the_config_and_is_finished_next_time()
    {
        // A directory where the second prompt file should go: the write throws part way through.
        // The entries have to still be in the config afterwards - they are the only copy of a
        // prompt that did not get written, and nothing else can seed them next time.
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "prompt.md"));

        var config = new MumblrConfig
        {
            PrebuiltCommands =
            [
                new PrebuiltCommand { Label = "Grammar", Text = "Mine, one." },
                new PrebuiltCommand { Label = "Prompt", Text = "Mine, two." },
            ],
        };

        Should.Throw<Exception>(() => PromptSeeding.SeedIfMissing(Library, config));

        config.PrebuiltCommands.ShouldNotBeNull();
        config.PrebuiltCommands.Count.ShouldBe(2);
        PromptSeeding.HasRun(directory).ShouldBeFalse();

        Directory.Delete(Path.Combine(directory, "prompt.md"));

        PromptSeeding.SeedIfMissing(Library, config).ShouldBeTrue();
        Library.Load().Prompts.Select(p => p.Text).ShouldBe(["Mine, one.", "Mine, two."]);
        config.PrebuiltCommands.ShouldBeNull();
    }

    [Fact]
    public void An_unfinished_seeding_does_not_overwrite_what_it_already_wrote()
    {
        // The first file is written, the second throws. Between the two runs the user edits the
        // one that landed. The retry leaves that edit alone; it writes the entry beside it rather
        // than over it, because it cannot tell an edited file of its own from somebody else's -
        // and a second button is visible and deletable, where a missing prompt is neither.
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "prompt.md"));

        var config = new MumblrConfig();

        Should.Throw<Exception>(() => PromptSeeding.SeedIfMissing(Library, config));

        var landed = Directory.GetFiles(directory, "*.md").ShouldHaveSingleItem();
        File.WriteAllText(landed, "---\nlabel: Grammar\norder: 10\n---\n\nEdited by hand.\n");

        Directory.Delete(Path.Combine(directory, "prompt.md"));
        PromptSeeding.SeedIfMissing(Library, config);

        Library.Load().Prompts.Select(p => p.Text).ShouldContain("Edited by hand.");
    }

    [Fact]
    public void A_half_written_file_does_not_cost_the_entry_that_belongs_in_it()
    {
        // The migration was killed mid-write and left a truncated grammar.md. Skipping the entry
        // over it and then dropping it from the config deletes a prompt that exists nowhere else.
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "grammar.md"), "---\nlabel: Gram");

        var config = new MumblrConfig
        {
            PrebuiltCommands = [new PrebuiltCommand { Label = "Grammar", Text = "Mine, and only here." }],
        };

        PromptSeeding.SeedIfMissing(Library, config);

        Library.Load().Prompts.Select(p => p.Text).ShouldContain("Mine, and only here.");
    }

    [Fact]
    public void A_name_that_is_already_taken_by_someone_else_does_not_cost_the_entry()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "shorter.md"), "Their own prompt.\n");

        var config = new MumblrConfig
        {
            PrebuiltCommands = [new PrebuiltCommand { Label = "Shorter", Text = "Halve the length." }],
        };

        PromptSeeding.SeedIfMissing(Library, config);

        var texts = Library.Load().Prompts.Select(p => p.Text).ToList();
        texts.ShouldContain("Their own prompt.");
        texts.ShouldContain("Halve the length.");
    }

    [Fact]
    public void An_entry_with_no_label_keeps_its_text()
    {
        // Reachable by hand editing, or by a "label": null that the loader strips. Only the text
        // makes it a prompt, and dropping the entry would take that text with it.
        var config = new MumblrConfig
        {
            PrebuiltCommands = [new PrebuiltCommand { Label = "  ", Text = "Still worth keeping." }],
        };

        PromptSeeding.SeedIfMissing(Library, config);

        Library.Load().Prompts.ShouldHaveSingleItem().Text.ShouldBe("Still worth keeping.");
    }

    [Fact]
    public void A_copy_of_our_own_text_in_another_encoding_is_not_counted_as_ours()
    {
        // Holds and Load have to agree about a file. If Holds said "ours" where Load refuses it,
        // the entry would be skipped over a file that never gets a button.
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "grammar.md");
        File.WriteAllText(path, Library.Render("Grammar", 10, "Fix grammar."), System.Text.Encoding.Unicode);

        PromptLibrary.Holds(path, Library.Render("Grammar", 10, "Fix grammar.")).ShouldBeFalse();
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

    public void Dispose()
    {
        if (File.Exists(directory))
            File.Delete(directory);

        TestDirectories.Delete(directory);
    }
}
