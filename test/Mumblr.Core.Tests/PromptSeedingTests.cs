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
    public void A_folder_that_appears_empty_under_the_seeding_does_not_clear_the_config()
    {
        // The whole reason the check above is about contents and not existence. The racer is
        // already spinning before the seeding starts and waits for the staging directory rather
        // than for a clock, and the write loop is long enough to be caught in.
        var mine = new MumblrConfig
        {
            PrebuiltCommands = [.. Enumerable.Range(0, 400)
                .Select(i => new PrebuiltCommand { Label = $"Prompt {i}", Text = $"Do thing {i}." })],
        };

        var parent = Path.GetDirectoryName(directory)!;
        var pattern = Path.GetFileName(directory) + ".*.tmp";

        using var spinning = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var racer = Task.Run(() =>
        {
            spinning.Set();
            while (!Directory.EnumerateDirectories(parent, pattern).Any())
            {
                stop.Token.ThrowIfCancellationRequested();
                Thread.Sleep(1);
            }

            Directory.CreateDirectory(directory);
        }, stop.Token);

        spinning.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        var thrown = Should.Throw<IOException>(() => PromptSeeding.SeedIfMissing(Library, mine));
        racer.Wait(TimeSpan.FromSeconds(10));

        thrown.ShouldNotBeNull();
        mine.PrebuiltCommands.ShouldNotBeNull();
        mine.PrebuiltCommands.Count.ShouldBe(400);
    }

    [Fact]
    public void A_folder_that_holds_prompts_means_another_window_got_there_first()
    {
        // Two windows starting at once. Directory.Move refuses an existing destination rather than
        // merging into it, but the prompts are on disk, which is what this was for - so the
        // entries still have to leave the config rather than be written back on the next save.
        Directory.CreateDirectory(directory);
        Library.Write("theirs", "Theirs", 10, "Their prompt.");

        PromptSeeding.AnotherWriterWon(directory, expected: 1).ShouldBeTrue();
    }

    [Fact]
    public void An_empty_folder_does_not_mean_another_window_got_there_first()
    {
        // Anything can create one in the window between the check and the move: a sync client
        // putting back a directory somebody deleted, a backup, the user. Reading that as "already
        // seeded" clears the config over nothing, and the folder then stops it ever trying again.
        Directory.CreateDirectory(directory);

        PromptSeeding.AnotherWriterWon(directory, expected: 1).ShouldBeFalse();
    }

    [Fact]
    public void With_nothing_to_write_an_empty_folder_is_enough()
    {
        Directory.CreateDirectory(directory);

        PromptSeeding.AnotherWriterWon(directory, expected: 0).ShouldBeTrue();
    }

    [Fact]
    public void A_folder_that_is_not_there_is_nobody_getting_there_first()
    {
        PromptSeeding.AnotherWriterWon(directory, expected: 1).ShouldBeFalse();
    }

    public void Dispose()
    {
        if (File.Exists(directory))
            File.Delete(directory);

        TestDirectories.Delete(directory);
    }
}
