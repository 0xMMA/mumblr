using Mumblr.Core.Text;

namespace Mumblr.Core.Tests;

public class TextPostProcessorTests
{
    [Fact]
    public void Replaces_a_dictated_mishearing()
    {
        var processor = new TextPostProcessor(new Dictionary<string, string> { ["clod code"] = "Claude Code" });

        processor.Apply("dann macht clod code das").ShouldBe("dann macht Claude Code das");
    }

    [Fact]
    public void Matches_case_insensitively()
    {
        var processor = new TextPostProcessor(new Dictionary<string, string> { ["clod code"] = "Claude Code" });

        processor.Apply("Clod Code startet").ShouldBe("Claude Code startet");
    }

    [Fact]
    public void Does_not_replace_inside_a_longer_word()
    {
        var processor = new TextPostProcessor(new Dictionary<string, string> { ["net"] = ".NET" });

        processor.Apply("Internet").ShouldBe("Internet");
    }

    [Fact]
    public void Applies_the_longest_rule_first()
    {
        var processor = new TextPostProcessor(new Dictionary<string, string>
        {
            ["code"] = "Code",
            ["clod code"] = "Claude Code",
        });

        processor.Apply("clod code").ShouldBe("Claude Code");
    }

    [Fact]
    public void Leaves_text_untouched_without_rules()
    {
        var processor = new TextPostProcessor(new Dictionary<string, string>());

        processor.Apply("nichts zu tun").ShouldBe("nichts zu tun");
    }

    [Fact]
    public void Replacement_containing_a_dollar_sign_is_literal()
    {
        var processor = new TextPostProcessor(new Dictionary<string, string> { ["dollar"] = "$1" });

        processor.Apply("dollar").ShouldBe("$1");
    }
}
