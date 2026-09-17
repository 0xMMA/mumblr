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
    public void An_unknown_frontmatter_key_costs_nothing()
    {
        // These are files people edit by hand. An unknown key is ignored, not refused.
        Given("grammar.md", "---\nlabel: Grammar\nmodel: opus\n\n---\nFix grammar.\n");

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();

        prompt.Label.ShouldBe("Grammar");
        prompt.Text.ShouldBe("Fix grammar.");
    }

    [Fact]
    public void A_prompt_that_opens_with_a_markdown_rule_keeps_its_first_paragraph()
    {
        // Read as frontmatter, this would swallow everything above the second rule and hand claude
        // half a prompt. Frontmatter is key-value lines; a paragraph is not.
        Given("steps.md", "---\n\nStep one.\n\n---\n\nStep two.\n");

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();

        prompt.Label.ShouldBe("steps");
        prompt.Text.ShouldBe("---\n\nStep one.\n\n---\n\nStep two.");
    }

    [Fact]
    public void An_empty_prompt_is_skipped_and_named()
    {
        // Sending it would hand claude the header prompt and no task at all.
        Given("empty.md", "---\nlabel: Empty\n---\n\n   \n");
        Given("real.md", "Do something.\n");

        var loaded = Library.Load();

        loaded.Prompts.ShouldHaveSingleItem().Label.ShouldBe("real");
        var skipped = loaded.Skipped.ShouldHaveSingleItem();
        Path.GetFileName(skipped.Path).ShouldBe("empty.md");
        skipped.Reason.ShouldBe("holds no prompt");
    }

    [Fact]
    public void A_comment_in_the_frontmatter_does_not_cost_the_label()
    {
        // These are files people edit by hand, and the block still names label and order.
        Given("grammar.md", "---\n# my notes\nlabel: Grammar\norder: 10\n---\n\nFix grammar.\n");

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();

        prompt.Label.ShouldBe("Grammar");
        prompt.Order.ShouldBe(10);
        prompt.Text.ShouldBe("Fix grammar.");
    }

    [Fact]
    public void A_prompt_that_opens_with_a_rule_keeps_it_even_when_it_reads_like_a_pair()
    {
        // "Rule: ..." is punctuated exactly like frontmatter and is not. What tells them apart is
        // that frontmatter names label or order; this names neither, so it is text.
        Given("rules.md", "---\nRule: always be concise\n---\n\nRewrite the text.\n");

        var prompt = Library.Load().Prompts.ShouldHaveSingleItem();

        prompt.Text.ShouldContain("Rule: always be concise");
        prompt.Text.ShouldContain("Rewrite the text.");
    }

    [Fact]
    public void A_byte_order_mark_does_not_turn_the_strict_decoder_off()
    {
        // StreamReader replaces the encoding it was handed the moment it sees a mark, with the
        // forgiving one - so the strictness would hold for exactly the files Windows editors do
        // not write. The mark itself must still not end up in the prompt.
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "bom-latin1.md"), [0xEF, 0xBB, 0xBF, 0x53, 0x63, 0x68, 0xF6, 0x6E, 0x0A]);
        File.WriteAllBytes(Path.Combine(directory, "bom-utf8.md"), [0xEF, 0xBB, 0xBF, 0x46, 0x69, 0x78, 0x2E, 0x0A]);

        var loaded = Library.Load();

        loaded.Prompts.ShouldHaveSingleItem().Text.ShouldBe("Fix.");
        Path.GetFileName(loaded.Skipped.ShouldHaveSingleItem().Path).ShouldBe("bom-latin1.md");
    }

    [Fact]
    public void A_file_that_is_not_utf8_is_skipped_rather_than_mangled()
    {
        // The default decoder turns bad bytes into replacement characters. A prompt is an
        // instruction that gets executed over the user's text; half of one must not be sent.
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "latin1.md"), [0x53, 0x63, 0x68, 0xF6, 0x6E, 0x20, 0x66, 0x69, 0x78, 0x0A]);

        var loaded = Library.Load();

        loaded.Prompts.ShouldBeEmpty();
        var skipped = loaded.Skipped.ShouldHaveSingleItem();
        Path.GetFileName(skipped.Path).ShouldBe("latin1.md");

        // Not "holds no prompt": that sends its owner looking for missing text, not at the encoding.
        skipped.Reason.ShouldBe("is not UTF-8");
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
