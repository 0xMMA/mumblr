using Mumblr.Core.Prompts;

namespace Mumblr.Core.Tests;

public class PromptLibraryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"mumblr-prompts-{Guid.NewGuid():N}");

    private PromptLibrary Library => new(directory);

    private void Given(string fileName, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    [Fact]
    public void A_directory_that_does_not_exist_is_no_prompts_rather_than_a_throw()
    {
        var loaded = Library.Load();

        loaded.Prompts.ShouldBeEmpty();
        loaded.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public void Frontmatter_names_the_button_and_the_body_is_the_prompt()
    {
        Given("grammar.md", "---\nlabel: Grammar\norder: 10\n---\n\nFix grammar and word order.\n");

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();

        prompt.Label.ShouldBe("Grammar");
        prompt.Order.ShouldBe(10);
        prompt.Text.ShouldBe("Fix grammar and word order.");
    }

    [Fact]
    public void The_order_in_the_frontmatter_beats_the_file_name()
    {
        Given("a-first-by-name.md", "---\nlabel: Second\norder: 20\n---\nBody.\n");
        Given("z-last-by-name.md", "---\nlabel: First\norder: 10\n---\nBody.\n");

        Library.Load().Prompts.Select(p => p.Label).ShouldBe(["First", "Second"]);
    }

    [Fact]
    public void A_file_without_an_order_sorts_after_the_ones_that_have_one()
    {
        Given("unordered.md", "---\nlabel: Later\n---\nBody.\n");
        Given("ordered.md", "---\nlabel: Sooner\norder: 99\n---\nBody.\n");

        Library.Load().Prompts.Select(p => p.Label).ShouldBe(["Sooner", "Later"]);
    }

    [Fact]
    public void A_file_with_no_frontmatter_is_a_prompt_named_after_itself()
    {
        Given("Clean up.md", "Remove the filler words.\n");

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();

        prompt.Label.ShouldBe("Clean up");
        prompt.Order.ShouldBe(PromptLibrary.NoOrder);
        prompt.Text.ShouldBe("Remove the filler words.");
    }

    [Fact]
    public void A_stray_frontmatter_line_costs_nothing()
    {
        // These are files people edit by hand. An unknown key is ignored, not refused.
        Given("grammar.md", "---\nlabel: Grammar\nmodel: opus\nnot a pair\n---\nFix grammar.\n");

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();

        prompt.Label.ShouldBe("Grammar");
        prompt.Text.ShouldBe("Fix grammar.");
    }

    [Fact]
    public void An_empty_prompt_is_skipped_and_named()
    {
        // Sending it would hand claude the header prompt and no task at all.
        Given("empty.md", "---\nlabel: Empty\n---\n\n   \n");
        Given("real.md", "Do something.\n");

        var loaded = Library.Load();

        loaded.Prompts.ShouldHaveSingleItem().Label.ShouldBe("real");
        Path.GetFileName(loaded.Skipped.ShouldHaveSingleItem()).ShouldBe("empty.md");
    }

    [Fact]
    public void Only_markdown_files_are_prompts()
    {
        Given("notes.txt", "Not a prompt.\n");
        Given("grammar.md", "Fix grammar.\n");

        Library.Load().Prompts.ShouldHaveSingleItem().Label.ShouldBe("grammar");
    }

    [Fact]
    public void What_is_written_is_what_is_read_back()
    {
        var path = Library.Write("grammar", "Grammar", 10, "Fix grammar and word order.");

        Path.GetExtension(path).ShouldBe(".md");

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();
        prompt.Label.ShouldBe("Grammar");
        prompt.Order.ShouldBe(10);
        prompt.Text.ShouldBe("Fix grammar and word order.");
    }

    [Fact]
    public void Writing_creates_the_directory()
    {
        Library.Write("grammar.md", "Grammar", 10, "Fix grammar.");

        Directory.Exists(directory).ShouldBeTrue();
    }

    public void Dispose() => TestDirectories.Delete(directory);
}
